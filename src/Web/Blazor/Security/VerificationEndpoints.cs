using System.Globalization;
using Microsoft.AspNetCore.Http;
using NexaVerify.Web.Services;

namespace NexaVerify.Web.Security;

/// <summary>
/// Document-verification parts of the backend-for-frontend: report and export downloads. The portal asks the API for a short-lived,
/// single-use link on the person's behalf, fetches the file on the server and sends it on. The browser never sees the link, a token
/// or the API address. Every relay needs the cookie, the report permission and a same-origin request, and has its own throttle bucket.
/// </summary>
public static class VerificationEndpoints
{
    /// <summary>Largest report or export the portal will pass on (a 5,000-row export is well below this).</summary>
    public const int MaxDownloadBytes = 8 * 1024 * 1024;

    public const string ThrottlePrefix = "verification-download:";

    public static void MapPortalVerification(this IEndpointRouteBuilder app)
    {
        app.MapGet("/bff/client/verification/{id:guid}/report",
                (HttpContext http, IVerificationDownloads downloads, DownloadThrottle throttle, Guid id, string? format) =>
                    format is "html" or "json"
                        ? RelayAsync(http, downloads, throttle, id, format == "json" ? VerificationDownloadKind.ReportJson : VerificationDownloadKind.ReportHtml)
                        : Reject(http, "Choose html or json."))
            .RequireAuthorization(Policies.Permission(WebPermissions.VerificationReportDownload), Policies.ClientPortal);

        app.MapGet("/bff/client/verification/{id:guid}/transactions.csv",
                (HttpContext http, IVerificationDownloads downloads, DownloadThrottle throttle, Guid id) =>
                    RelayAsync(http, downloads, throttle, id, VerificationDownloadKind.TransactionsCsv))
            .RequireAuthorization(Policies.Permission(WebPermissions.VerificationReportDownload), Policies.ClientPortal);

        // Platform staff: the operations numbers as a spreadsheet, through the shared CSV relay.
        app.MapGet("/bff/admin/verification/stats.csv",
                (HttpContext http, IApiGateway api, DownloadThrottle throttle, string? from, string? to) =>
                    DownloadEndpoints.RelayCsvAsync(http, api, throttle, from, to, "admin/verifications/stats/export.csv", "nexaverify-verification"))
            .RequireAuthorization(Policies.Permission(WebPermissions.VerificationAdminRead), Policies.PlatformPortal);
    }

    private static async Task Reject(HttpContext http, string message)
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        http.Response.Headers.CacheControl = "no-store";
        http.Response.ContentType = "text/plain; charset=utf-8";
        await http.Response.WriteAsync(message, http.RequestAborted);
    }

    private static async Task Fail(HttpContext http, int status, string message)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "text/plain; charset=utf-8";
        await http.Response.WriteAsync(message, http.RequestAborted);
    }

    private static async Task RelayAsync(HttpContext http, IVerificationDownloads downloads, DownloadThrottle throttle, Guid id, VerificationDownloadKind kind)
    {
        http.Response.Headers.CacheControl = "no-store";

        // Only the portal's own pages (or the address bar) may start a download: another website cannot make a signed-in browser fetch one.
        if (!AuthEndpoints.IsSameOriginNavigation(http.Request))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var sessionId = http.User.FindFirst(PortalClaims.SessionId)?.Value;
        if (!throttle.TryAcquire(ThrottlePrefix + (sessionId ?? http.User.Identity?.Name ?? "anonymous"), out var retryAfter))
        {
            http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            await Fail(http, StatusCodes.Status429TooManyRequests, "You have started several downloads in a row. Please wait a minute and try again.");
            return;
        }

        var options = new ApiCallOptions { SessionId = sessionId, ClientIp = http.Connection.RemoteIpAddress?.ToString() };
        var link = await downloads.CreateLinkAsync(id, kind, options, http.RequestAborted);
        if (!link.IsSuccess)
        {
            var error = link.Error!;
            await Fail(http, error.Status ?? StatusCodes.Status502BadGateway, error.CorrelationId is null ? error.Message : $"{error.Message} (ref {error.CorrelationId})");
            return;
        }

        var opened = await downloads.OpenAsync(link.Value.DownloadUrl, kind, options, http.RequestAborted);
        if (!opened.IsSuccess)
        {
            var error = opened.Error!;
            await Fail(http, error.Status ?? StatusCodes.Status502BadGateway, error.CorrelationId is null ? error.Message : $"{error.Message} (ref {error.CorrelationId})");
            return;
        }

        using var upstream = opened.Value;
        if (!string.Equals(upstream.Content.Headers.ContentType?.MediaType, kind.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            await Fail(http, StatusCodes.Status502BadGateway, "The download could not be prepared right now. Please try again later.");
            return;
        }

        // Read it in full (bounded) before sending anything, so a truncated or oversized file is never delivered half-way.
        byte[] file;
        try
        {
            await using var body = await upstream.Content.ReadAsStreamAsync(http.RequestAborted);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, http.RequestAborted)) > 0)
            {
                if (buffer.Length + read > MaxDownloadBytes)
                {
                    await Fail(http, StatusCodes.Status502BadGateway, "The download could not be prepared right now. Please try again later.");
                    return;
                }

                buffer.Write(chunk, 0, read);
            }

            file = buffer.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        // Always an attachment under a locked-down policy of its own: nothing the file contains can run in, or reach, the portal.
        http.Items[SecurityHeadersMiddleware.CspOverrideItemKey] = BillingEndpoints.InvoiceCsp;
        http.Response.Headers["Content-Security-Policy"] = BillingEndpoints.InvoiceCsp;
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"nexaverify-check-{id:N}.{kind.Extension}\"";
        http.Response.ContentType = kind.ContentType + "; charset=utf-8";
        http.Response.ContentLength = file.Length;
        await http.Response.Body.WriteAsync(file, http.RequestAborted);
    }
}
