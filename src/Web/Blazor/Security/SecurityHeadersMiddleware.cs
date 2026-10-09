using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace NexaVerify.Web.Security;

/// <summary>
/// Adds the security headers from docs/04 §5 and a per-request CSP nonce. Scripts: 'self' + nonce only (no 'unsafe-inline').
/// Styles keep 'unsafe-inline' because MudBlazor positions popovers/overlays with style attributes.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IOptions<SecurityHeadersOptions> options)
{
    public const string NonceItemKey = "nv.csp-nonce";

    /// <summary>An endpoint that serves foreign content (the invoice relay) puts its own, stricter policy here; it replaces the page policy.</summary>
    public const string CspOverrideItemKey = "nv.csp-override";

    /// <summary>The only page that embeds the sign-up bot check.</summary>
    public const string SignupPath = "/signup";

    /// <summary>The bot-check provider's origin, allowed (scripts, frames, connections) on the sign-up page only and only when enabled.</summary>
    public const string TurnstileOrigin = "https://challenges.cloudflare.com";

    private readonly SecurityHeadersOptions _options = options.Value;

    public Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled)
        {
            return next(context);
        }

        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        context.Items[NonceItemKey] = nonce;

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            if (_options.ContentSecurityPolicyEnabled)
            {
                headers["Content-Security-Policy"] = context.Items[CspOverrideItemKey] as string ?? BuildCsp(_options, nonce, _options.TurnstileEnabled && context.Request.Path.StartsWithSegments(SignupPath, StringComparison.OrdinalIgnoreCase));
            }

            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = _options.CameraAllowedForSelf
                ? "camera=(self), microphone=(), geolocation=()"
                : "camera=(), microphone=(), geolocation=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["X-Frame-Options"] = "DENY";
            if (!headers.ContainsKey("Cache-Control") && context.Request.Path.StartsWithSegments("/_blazor"))
            {
                headers["Cache-Control"] = "no-store";
            }

            return Task.CompletedTask;
        });

        return next(context);
    }

    public static string BuildCsp(SecurityHeadersOptions options, string nonce, bool allowCaptcha = false)
    {
        var connect = new List<string> { "'self'", options.AllowInsecureWebSockets ? "ws: wss:" : "wss:" };
        if (!string.IsNullOrWhiteSpace(options.ApiOrigin))
        {
            connect.Add(options.ApiOrigin.Trim());
        }

        if (allowCaptcha)
        {
            connect.Add(TurnstileOrigin);
        }

        var script = $"'self' 'nonce-{nonce}'" + (string.IsNullOrWhiteSpace(options.ExtraScriptSrc) ? string.Empty : " " + options.ExtraScriptSrc.Trim())
                     + (allowCaptcha ? " " + TurnstileOrigin : string.Empty);
        var frame = allowCaptcha ? $"frame-src {TurnstileOrigin}" : null;

        return string.Join("; ", new[] {
            "default-src 'self'",
            $"script-src {script}",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data: blob:",
            "font-src 'self' data:",
            "media-src 'self' blob:",
            frame,
            $"connect-src {string.Join(' ', connect)}",
            "frame-ancestors 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            "object-src 'none'" }.Where(d => d is not null));
    }
}

public static class HttpContextNonceExtensions
{
    /// <summary>The CSP nonce for this request (empty when headers are disabled). Put it on every inline script tag.</summary>
    public static string GetCspNonce(this HttpContext? context) =>
        context?.Items[SecurityHeadersMiddleware.NonceItemKey] as string ?? string.Empty;
}
