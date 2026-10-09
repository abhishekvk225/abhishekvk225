using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using NexaVerify.Web.Services;

namespace NexaVerify.Web.Security;

/// <summary>
/// Billing parts of the backend-for-frontend: the invoice and CSV relays, and the small same-site hop that lets a person who comes back
/// from the payment partner's page keep their sign-in. The browser never gets a token; every relay needs the cookie, the billing
/// permission and a same-origin request.
/// </summary>
public static class BillingEndpoints
{
    /// <summary>Largest invoice page the portal will pass on (a real one is a few KB).</summary>
    public const int MaxInvoiceBytes = 1024 * 1024;

    /// <summary>
    /// The policy the invoice is served under. <c>sandbox</c> without any allow-flag gives the page a unique opaque origin: no scripts,
    /// no forms, no top navigation, no access to the portal's cookies or storage, even if the HTML contained something nasty.
    /// Inline styles and embedded images are all an invoice needs.
    /// </summary>
    public const string InvoiceCsp = "sandbox; default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public const string InvoicePrefix = "invoice:";

    public static void MapPortalBilling(this IEndpointRouteBuilder app)
    {
        // The API derives the client from the credential; the portal only relays the file.
        app.MapGet("/bff/client/billing/orders/export.csv",
                (HttpContext http, IApiGateway api, DownloadThrottle throttle, string? from, string? to) =>
                    DownloadEndpoints.RelayCsvAsync(http, api, throttle, from, to, "client/billing/orders/export.csv", "nexaverify-orders"))
            .RequireAuthorization(Policies.Permission(WebPermissions.BillingRead), Policies.ClientPortal);

        app.MapGet("/bff/client/billing/orders/{id:guid}/invoice", RelayInvoiceAsync)
            .RequireAuthorization(Policies.Permission(WebPermissions.BillingRead), Policies.ClientPortal);
    }

    private static async Task RelayInvoiceAsync(HttpContext http, IApiGateway api, DownloadThrottle throttle, Guid id)
    {
        http.Response.Headers.CacheControl = "no-store";

        // Only the portal's own pages (or the address bar) may open an invoice: another website cannot make a signed-in browser fetch one.
        if (!AuthEndpoints.IsSameOriginNavigation(http.Request))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var sessionId = http.User.FindFirst(PortalClaims.SessionId)?.Value;
        var key = InvoicePrefix + (sessionId ?? http.User.Identity?.Name ?? "anonymous");
        if (!throttle.TryAcquire(key, out var retryAfter))
        {
            http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            http.Response.ContentType = "text/plain; charset=utf-8";
            await http.Response.WriteAsync("You have opened several invoices in a row. Please wait a minute and try again.", http.RequestAborted);
            return;
        }

        var result = await api.OpenStreamAsync($"client/billing/orders/{id}/invoice", http.RequestAborted,
            new ApiCallOptions { SessionId = sessionId, ClientIp = http.Connection.RemoteIpAddress?.ToString(), Accept = "text/html" });
        if (!result.IsSuccess)
        {
            var error = result.Error!;
            http.Response.StatusCode = error.Status ?? StatusCodes.Status502BadGateway;
            http.Response.ContentType = "text/plain; charset=utf-8";
            await http.Response.WriteAsync(error.CorrelationId is null ? error.Message : $"{error.Message} (ref {error.CorrelationId})", http.RequestAborted);
            return;
        }

        using var upstream = result.Value;
        if (!string.Equals(upstream.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
        {
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
            http.Response.ContentType = "text/plain; charset=utf-8";
            await http.Response.WriteAsync("The invoice could not be shown right now. Please try again later.", http.RequestAborted);
            return;
        }

        // Read it in full (bounded) before sending anything, so a truncated or oversized document is never delivered half-way.
        byte[] page;
        try
        {
            await using var body = await upstream.Content.ReadAsStreamAsync(http.RequestAborted);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, http.RequestAborted)) > 0)
            {
                if (buffer.Length + read > MaxInvoiceBytes)
                {
                    http.Response.StatusCode = StatusCodes.Status502BadGateway;
                    http.Response.ContentType = "text/plain; charset=utf-8";
                    await http.Response.WriteAsync("The invoice could not be shown right now. Please try again later.", http.RequestAborted);
                    return;
                }

                buffer.Write(chunk, 0, read);
            }

            page = buffer.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        // The page is shown on its own, never inside the portal: a locked-down policy of its own (it replaces the portal's), no sniffing,
        // not embeddable, not shared with other sites.
        http.Items[SecurityHeadersMiddleware.CspOverrideItemKey] = InvoiceCsp;
        http.Response.Headers["Content-Security-Policy"] = InvoiceCsp;
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        http.Response.Headers.ContentDisposition = $"inline; filename=\"nexaverify-invoice-{id:N}.html\"";
        http.Response.ContentType = "text/html; charset=utf-8";
        http.Response.ContentLength = page.Length;
        await http.Response.Body.WriteAsync(page, http.RequestAborted);
    }

    // ---- coming back from the payment partner ---------------------------------------------------------------------------------

    public const string ReturnPath = "/client/billing/return";

    /// <summary>
    /// The sign-in cookie is <c>SameSite=Strict</c>, so a browser that arrives from another website (the payment partner) sends no
    /// cookie and would see a sign-in page. For exactly this one address, a cross-site arrival gets a tiny page that moves on to the
    /// same address by itself: that second request starts from our own page, so the cookie travels. Nothing is trusted from the
    /// query string; only a well-formed order id and one of two result words are carried over, rebuilt by us.
    /// </summary>
    public static IApplicationBuilder UseBillingReturnHop(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (HttpMethods.IsGet(request.Method)
                && request.Path.Equals(ReturnPath, StringComparison.OrdinalIgnoreCase)
                && request.Headers["Sec-Fetch-Site"].ToString() == "cross-site"
                && !request.Query.ContainsKey("hop"))
            {
                await WriteHopAsync(context);
                return;
            }

            await next(context);
        });

    internal static string HopTarget(IQueryCollection query)
    {
        var target = new StringBuilder(ReturnPath).Append("?hop=1");
        if (Guid.TryParse(query["order"].ToString(), out var order))
        {
            target.Append("&order=").Append(order.ToString("D"));
        }

        var result = query["result"].ToString();
        if (result is "success" or "cancelled")
        {
            target.Append("&result=").Append(result);
        }

        return target.ToString();
    }

    private static async Task WriteHopAsync(HttpContext context)
    {
        var target = HopTarget(context.Request.Query);
        var html = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Returning to NexaVerify</title>"
                   + $"<meta http-equiv=\"refresh\" content=\"0;url={WebUtility.HtmlEncode(target)}\"><meta name=\"robots\" content=\"noindex\"></head>"
                   + $"<body><p>Returning to NexaVerify&hellip; <a href=\"{WebUtility.HtmlEncode(target)}\">Continue</a></p></body></html>";
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(html, context.RequestAborted);
    }
}
