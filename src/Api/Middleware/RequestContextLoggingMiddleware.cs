using NexaVerify.Application.Abstractions;
using Serilog.Context;

namespace NexaVerify.Api.Middleware;

/// <summary>Enriches logs with the actor and tenant after authentication (ids only — never emails or tokens).</summary>
public sealed class RequestContextLoggingMiddleware
{
    private readonly RequestDelegate _next;

    public RequestContextLoggingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ICurrentUser user, ITenantContext tenant)
    {
        using (LogContext.PushProperty("ActorType", user.ActorType.ToString()))
        using (LogContext.PushProperty("ActorId", user.ActorId))
        using (LogContext.PushProperty("ClientId", tenant.ClientId))
        {
            await _next(context);
        }
    }
}
