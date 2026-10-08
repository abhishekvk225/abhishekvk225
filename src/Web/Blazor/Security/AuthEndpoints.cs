using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using NexaVerify.Web.Services;

namespace NexaVerify.Web.Security;

/// <summary>Reasons the sign-in form can fail, expressed as query codes on <c>/login</c> (no account enumeration).</summary>
public static class LoginErrors
{
    public const string Invalid = "invalid";
    public const string Throttled = "throttled";
    public const string Blocked = "blocked";
    public const string Unavailable = "unavailable";
    public const string Expired = "expired";
    public const string SessionEnded = "ended";

    public static string FromStatus(int? status) => status switch
    {
        429 => Throttled,
        403 => Blocked,
        >= 500 or null => Unavailable,
        _ => Invalid, // 400/401/404 all look the same: wrong email or password
    };

    public static string MessageFor(string? code) => code switch
    {
        Invalid => "The email or password is not correct.",
        Throttled => "Too many sign-in attempts. Please wait a few minutes and try again.",
        Blocked => "This account can't sign in right now. Please contact your administrator.",
        Unavailable => "We couldn't sign you in right now. Please try again in a moment.",
        Expired => "Your sign-in page expired. Please try again.",
        SessionEnded => "You were signed out because your session ended. Please sign in again.",
        _ => string.Empty,
    };
}

/// <summary>
/// The only places that touch the cookie: sign-in and sign-out. They are plain HTTP endpoints (not Blazor circuits) because a
/// SignalR circuit cannot set cookies. Everything else works with the session through the server-side store.
/// </summary>
public static partial class AuthEndpoints
{
    private static readonly Regex ReferencePattern = ReferenceRegex();

    public static void MapPortalAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/login", LoginAsync).DisableAntiforgery().AllowAnonymous();
        app.MapGet("/auth/signed-out", SignOutAsync).AllowAnonymous();
    }

    /// <summary>A correlation reference is shown as text on the login page; only a conservative shape is accepted.</summary>
    public static string? SanitiseReference(string? value) => value is not null && ReferencePattern.IsMatch(value) ? value : null;

    private static async Task<IResult> LoginAsync(HttpContext http, IAntiforgery antiforgery, IPortalAuth auth, ISessionStore store, TimeProvider clock, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger("NexaVerify.Web.Login");
        if (!http.Request.HasFormContentType)
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        try
        {
            await antiforgery.ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.Redirect(LoginUrl(LoginErrors.Expired, null, null));
        }

        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var email = form["email"].ToString();
        var password = form["password"].ToString();
        var returnUrl = form["returnUrl"].ToString();
        var safeReturn = ReturnUrl.IsSafe(returnUrl) ? returnUrl : null;

        if (string.IsNullOrWhiteSpace(email) || email.Length > 256 || string.IsNullOrEmpty(password) || password.Length > 256)
        {
            return Results.Redirect(LoginUrl(LoginErrors.Invalid, null, safeReturn));
        }

        var result = await auth.SignInAsync(email, password, http.RequestAborted, http.Connection.RemoteIpAddress?.ToString());
        if (!result.IsSuccess)
        {
            var code = LoginErrors.FromStatus(result.Error!.Status);
            logger.LogInformation("Portal sign-in failed ({Reason}, status {Status}).", code, result.Error.Status);
            return Results.Redirect(LoginUrl(code, SanitiseReference(result.Error.CorrelationId), safeReturn));
        }

        // A browser that was still signed in as someone else loses that session now.
        var previous = http.User.FindFirst(PortalClaims.SessionId)?.Value;
        if (!string.IsNullOrEmpty(previous))
        {
            await store.RemoveAsync(previous);
        }

        var session = result.Value;
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            PortalPrincipalFactory.ForCookie(session.Id),
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = false, ExpiresUtc = session.AbsoluteExpiresAt });

        var target = session.MustChangePassword ? "/change-password" : ReturnUrl.ResolveForPortal(safeReturn, session.Portal);
        return Results.Redirect(target);
    }

    private static async Task<IResult> SignOutAsync(HttpContext http, IPortalAuth auth, string? reason)
    {
        var sessionId = http.User.FindFirst(PortalClaims.SessionId)?.Value;
        if (!string.IsNullOrEmpty(sessionId))
        {
            await auth.SignOutAsync(sessionId, CancellationToken.None);
        }

        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Redirect(reason == "expired" ? LoginUrl(LoginErrors.SessionEnded, null, null) : "/login?signedOut=1");
    }

    internal static string LoginUrl(string? error, string? reference, string? returnUrl)
    {
        var parts = new List<string>();
        if (error is not null)
        {
            parts.Add("error=" + Uri.EscapeDataString(error));
        }

        if (reference is not null)
        {
            parts.Add("ref=" + Uri.EscapeDataString(reference));
        }

        if (returnUrl is not null)
        {
            parts.Add("returnUrl=" + Uri.EscapeDataString(returnUrl));
        }

        return parts.Count == 0 ? "/login" : "/login?" + string.Join('&', parts);
    }

    [GeneratedRegex(@"^[A-Za-z0-9\-_.:]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 200)]
    private static partial Regex ReferenceRegex();
}

/// <summary>BFF download endpoints: the API's CSV is streamed through the portal so the browser never sees a bearer token.</summary>
public static class DownloadEndpoints
{
    public static void MapPortalDownloads(this IEndpointRouteBuilder app)
    {
        app.MapGet("/bff/reports/usage.csv", UsageCsvAsync)
            .RequireAuthorization(Policies.Permission(WebPermissions.ReportsRead), Policies.PlatformPortal);
    }

    private static async Task UsageCsvAsync(HttpContext http, IApiGateway api, string? from, string? to)
    {
        var query = new List<string>();
        foreach (var (name, value) in new[] { ("from", from), ("to", to) })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsync("Dates must look like 2026-01-31.", http.RequestAborted);
                return;
            }

            query.Add($"{name}={date:yyyy-MM-dd}");
        }

        var sessionId = http.User.FindFirst(PortalClaims.SessionId)?.Value;
        var path = "admin/reports/usage.csv" + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty);
        var result = await api.OpenStreamAsync(path, http.RequestAborted, new ApiCallOptions { SessionId = sessionId });
        if (!result.IsSuccess)
        {
            var error = result.Error!;
            http.Response.StatusCode = error.Status ?? StatusCodes.Status502BadGateway;
            http.Response.ContentType = "text/plain; charset=utf-8";
            await http.Response.WriteAsync(error.CorrelationId is null ? error.Message : $"{error.Message} (ref {error.CorrelationId})", http.RequestAborted);
            return;
        }

        using var upstream = result.Value;
        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"nexaverify-usage-{DateTime.UtcNow:yyyyMMdd}.csv\"";
        http.Response.Headers.CacheControl = "no-store";
        await using var body = await upstream.Content.ReadAsStreamAsync(http.RequestAborted);
        await body.CopyToAsync(http.Response.Body, http.RequestAborted);
    }
}
