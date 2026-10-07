namespace NexaVerify.Api.Middleware;

/// <summary>
/// Redirects plain-HTTP requests to HTTPS. Unlike UseHttpsRedirection it needs no configured https port, so it works behind a
/// TLS-terminating proxy once forwarded headers are trusted. Health probes (in-cluster, plain HTTP) are exempt.
/// The host used for the redirect is the validated Host header (AllowedHosts).
/// </summary>
public sealed class HttpsEnforcementMiddleware
{
    private readonly RequestDelegate _next;

    public HttpsEnforcementMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.IsHttps || context.Request.Path.StartsWithSegments("/health"))
        {
            return _next(context);
        }

        var request = context.Request;
        var target = $"https://{request.Host}{request.PathBase}{request.Path}{request.QueryString}";
        context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
        context.Response.Headers.Location = target;
        return Task.CompletedTask;
    }
}
