using Microsoft.Extensions.Options;
using NexaVerify.Api.Configuration;

namespace NexaVerify.Api.Middleware;

/// <summary>Adds hardening headers to every response. All values come from configuration.</summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SecurityHeadersOptions _options;

    public SecurityHeadersMiddleware(RequestDelegate next, IOptions<SecurityHeadersOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled)
        {
            return _next(context);
        }

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = _options.ReferrerPolicy;
            headers["Permissions-Policy"] = _options.PermissionsPolicy;
            headers["Cross-Origin-Opener-Policy"] = "same-origin";

            // Swagger UI needs its own inline assets; it is only mapped outside production.
            if (!context.Request.Path.StartsWithSegments("/swagger"))
            {
                headers["Content-Security-Policy"] = _options.ContentSecurityPolicy;
            }

            if (!headers.ContainsKey("Cache-Control"))
            {
                headers["Cache-Control"] = "no-store";
            }

            if (_options.HstsMaxAgeSeconds > 0 && context.Request.IsHttps)
            {
                headers["Strict-Transport-Security"] = $"max-age={_options.HstsMaxAgeSeconds}; includeSubDomains";
            }

            return Task.CompletedTask;
        });

        return _next(context);
    }
}
