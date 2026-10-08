using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
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
    public const string MfaInvalid = "mfa-invalid";
    public const string MfaExpired = "mfa-expired";

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
        MfaInvalid => "That code is not right. Check your authenticator app (or use a recovery code) and try again.",
        MfaExpired => "Your sign-in timed out or was used up. Please sign in again.",
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
        app.MapPost("/auth/mfa", MfaAsync).DisableAntiforgery().AllowAnonymous();
        app.MapGet("/auth/signed-out", SignOutAsync).AllowAnonymous();
    }

    /// <summary>A correlation reference is shown as text on the login page; only a conservative shape is accepted.</summary>
    public static string? SanitiseReference(string? value) => value is not null && ReferencePattern.IsMatch(value) ? value : null;

    /// <summary>Name of the opaque cookie that names the parked second-factor step (never the challenge itself).</summary>
    public static string MfaCookieName(bool secure) => secure ? "__Host-nv.mfa" : "nv.mfa";

    private static async Task<IResult> LoginAsync(
        HttpContext http, IAntiforgery antiforgery, IPortalAuth auth, ISessionStore store, IMfaPendingStore pendingStore,
        IOptions<CookieSecurityOptions> cookies, TimeProvider clock, ILoggerFactory loggers)
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

        var result = await auth.BeginSignInAsync(email, password, http.RequestAborted, http.Connection.RemoteIpAddress?.ToString());
        if (!result.IsSuccess)
        {
            var code = LoginErrors.FromStatus(result.Error!.Status);
            logger.LogInformation("Portal sign-in failed ({Reason}, status {Status}).", code, result.Error.Status);
            return Results.Redirect(LoginUrl(code, SanitiseReference(result.Error.CorrelationId), safeReturn));
        }

        if (result.Value.Pending is { } pending)
        {
            // Password accepted, second factor still to come. The API's challenge stays on the server; the browser only gets an opaque
            // cookie naming it. Nothing identifies the person yet, so there is no session and no authenticated state.
            var id = await pendingStore.CreateAsync(pending with { ReturnUrl = safeReturn }, http.RequestAborted);
            AppendMfaCookie(http, cookies.Value, id, pending.ExpiresAt);
            return Results.Redirect("/login/mfa");
        }

        return await CompleteSignInAsync(http, store, result.Value.Session!, safeReturn);
    }

    private static async Task<IResult> MfaAsync(
        HttpContext http, IAntiforgery antiforgery, IPortalAuth auth, ISessionStore store, IMfaPendingStore pendingStore,
        IOptions<CookieSecurityOptions> cookies, ILoggerFactory loggers)
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

        var cookieName = MfaCookieName(cookies.Value.RequireSecure);
        var pendingId = http.Request.Cookies[cookieName];
        var pending = await pendingStore.GetAsync(pendingId, http.RequestAborted);
        if (pending is null)
        {
            http.Response.Cookies.Delete(cookieName);
            return Results.Redirect(LoginUrl(LoginErrors.MfaExpired, null, null));
        }

        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var code = form["code"].ToString().Trim();
        if (code.Length is 0 or > 32)
        {
            return Results.Redirect("/login/mfa?error=" + LoginErrors.MfaInvalid);
        }

        var result = await auth.CompleteMfaSignInAsync(pending, code, http.RequestAborted);
        if (!result.IsSuccess)
        {
            var error = result.Error!;
            logger.LogInformation("Portal second factor failed (status {Status}, code {Code}).", error.Status, error.Code);
            if (error.Code == "MFA_CODE_INVALID" || error.Status == 400)
            {
                return Results.Redirect("/login/mfa?error=" + LoginErrors.MfaInvalid); // the challenge is still alive: try again
            }

            if (error.Status is 429 or >= 500 or null)
            {
                return Results.Redirect("/login/mfa?error=" + LoginErrors.FromStatus(error.Status));
            }

            // The challenge is dead (expired, used up, or the account was locked/blocked meanwhile): start over.
            await pendingStore.RemoveAsync(pendingId, CancellationToken.None);
            http.Response.Cookies.Delete(cookieName);
            return Results.Redirect(LoginUrl(error.Status == 403 ? LoginErrors.Blocked : LoginErrors.MfaExpired, SanitiseReference(error.CorrelationId), pending.ReturnUrl));
        }

        await pendingStore.RemoveAsync(pendingId, CancellationToken.None);
        http.Response.Cookies.Delete(cookieName);
        return await CompleteSignInAsync(http, store, result.Value, pending.ReturnUrl);
    }

    private static async Task<IResult> CompleteSignInAsync(HttpContext http, ISessionStore store, PortalSession session, string? safeReturn)
    {
        // A browser that was still signed in as someone else loses that session now.
        var previous = http.User.FindFirst(PortalClaims.SessionId)?.Value;
        if (!string.IsNullOrEmpty(previous))
        {
            await store.RemoveAsync(previous);
        }

        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            PortalPrincipalFactory.ForCookie(session.Id),
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = false, ExpiresUtc = session.AbsoluteExpiresAt });

        var target = session.MustChangePassword ? "/change-password"
            : session.MfaEnrolmentRequired ? "/mfa/enroll"
            : ReturnUrl.ResolveForPortal(safeReturn, session.Portal);
        return Results.Redirect(target);
    }

    private static void AppendMfaCookie(HttpContext http, CookieSecurityOptions cookies, string id, DateTimeOffset expires) =>
        http.Response.Cookies.Append(MfaCookieName(cookies.RequireSecure), id, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            Path = "/",
            Secure = cookies.RequireSecure,
            SameSite = cookies.SameSiteMode,
            Expires = expires,
        });

    private static async Task<IResult> SignOutAsync(HttpContext http, IPortalAuth auth, string? reason)
    {
        // Signing out changes state, so a page on another site must not be able to trigger it by linking here. Browsers tell us who
        // initiated the request; only the portal itself (or the user typing the address) may sign out.
        if (!IsSameOriginNavigation(http.Request))
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var sessionId = http.User.FindFirst(PortalClaims.SessionId)?.Value;
        if (!string.IsNullOrEmpty(sessionId))
        {
            await auth.SignOutAsync(sessionId, CancellationToken.None);
        }

        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Redirect(reason == "expired" ? LoginUrl(LoginErrors.SessionEnded, null, null) : "/login?signedOut=1");
    }

    /// <summary>True when the browser says the request came from this site (or from the address bar). A missing header is refused.</summary>
    public static bool IsSameOriginNavigation(HttpRequest request) =>
        request.Headers["Sec-Fetch-Site"].ToString() is "same-origin" or "none";

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

/// <summary>
/// Caps how often one signed-in session may start an export through the portal (each one is a database-heavy report on the API).
/// Fixed window per session id, bounded memory. The API still audits every export it serves.
/// </summary>
public sealed class DownloadThrottle(TimeProvider clock)
{
    public const int MaxPerWindow = 6;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private const int PruneAbove = 2_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _windows = new(StringComparer.Ordinal);

    /// <summary>True when the export may start; otherwise <paramref name="retryAfter"/> says when the window reopens.</summary>
    public bool TryAcquire(string key, out TimeSpan retryAfter)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_windows.Count > PruneAbove)
            {
                foreach (var stale in _windows.Where(w => now - w.Value.Start >= Window).Select(w => w.Key).ToList())
                {
                    _windows.Remove(stale);
                }
            }

            if (!_windows.TryGetValue(key, out var current) || now - current.Start >= Window)
            {
                _windows[key] = (now, 1);
                retryAfter = TimeSpan.Zero;
                return true;
            }

            if (current.Count >= MaxPerWindow)
            {
                retryAfter = current.Start + Window - now;
                return false;
            }

            _windows[key] = (current.Start, current.Count + 1);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }
}

/// <summary>BFF download endpoints: the API's CSV is streamed through the portal so the browser never sees a bearer token.</summary>
public static class DownloadEndpoints
{
    public static void MapPortalDownloads(this IEndpointRouteBuilder app)
    {
        app.MapGet("/bff/reports/usage.csv", (HttpContext http, IApiGateway api, DownloadThrottle throttle, string? from, string? to) => RelayCsvAsync(http, api, throttle, from, to, "admin/reports/usage.csv"))
            .RequireAuthorization(Policies.Permission(WebPermissions.ReportsRead), Policies.PlatformPortal);

        // The client portal's own usage export: the API derives the client from the credential, the portal only relays the file.
        app.MapGet("/bff/client/reports/usage.csv", (HttpContext http, IApiGateway api, DownloadThrottle throttle, string? from, string? to) => RelayCsvAsync(http, api, throttle, from, to, "client/reports/usage.csv"))
            .RequireAuthorization(Policies.Permission(WebPermissions.UsageRead), Policies.ClientPortal);
    }

    private static async Task RelayCsvAsync(HttpContext http, IApiGateway api, DownloadThrottle throttle, string? from, string? to, string apiPath)
    {
        // Only the portal's own pages (or the address bar) may start an export: a link on another site cannot make a signed-in
        // browser trigger a heavy report.
        if (!AuthEndpoints.IsSameOriginNavigation(http.Request))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            http.Response.Headers.CacheControl = "no-store";
            return;
        }

        var key = http.User.FindFirst(PortalClaims.SessionId)?.Value ?? http.User.Identity?.Name ?? "anonymous";
        if (!throttle.TryAcquire(key, out var retryAfter))
        {
            http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            http.Response.Headers.CacheControl = "no-store";
            http.Response.ContentType = "text/plain; charset=utf-8";
            await http.Response.WriteAsync("You have started several reports in a row. Please wait a minute and try again.", http.RequestAborted);
            return;
        }

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
        var path = apiPath + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty);
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
