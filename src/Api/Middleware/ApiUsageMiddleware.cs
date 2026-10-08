using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using NexaVerify.Api.Http;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Api;
using NexaVerify.Contracts.Common;

namespace NexaVerify.Api.Middleware;

/// <summary>
/// For API-key calls and the face endpoints: enforces the per-credential rate limit and the per-client daily quota, then records
/// the call (route template, status, timing — never bodies or query strings) for the client's usage screens.
/// </summary>
public sealed class ApiUsageMiddleware
{
    private const string FacePrefix = "/api/v1/faces";

    private readonly RequestDelegate _next;

    public ApiUsageMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ICurrentUser user, IApiUsageLimiter limiter, IApiRequestLogSink sink, TimeProvider time)
    {
        if (user is not { IsAuthenticated: true, ClientId: { } clientId } || !IsMetered(context, user))
        {
            await _next(context);
            return;
        }

        var credential = user.ActorId ?? clientId;
        int? keyLimit = user.ActorType == ActorType.ApiKey && int.TryParse(context.User.FindFirst(NexaClaims.RateLimitPerMinute)?.Value, out var parsed) ? parsed : null;
        var started = Stopwatch.GetTimestamp();

        var (error, retryAfter) = await limiter.TryAcquireAsync(clientId, credential, keyLimit, context.RequestAborted);
        if (error is not null)
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(ApiProblem.Create(context, StatusCodes.Status429TooManyRequests, error.Code, error.Message), options: null, contentType: "application/problem+json");
            Record(context, user, clientId, sink, time, started);
            return;
        }

        try
        {
            await _next(context);
        }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            throw;
        }
        finally
        {
            Record(context, user, clientId, sink, time, started);
        }
    }

    private static bool IsMetered(HttpContext context, ICurrentUser user) =>
        user.ActorType == ActorType.ApiKey || context.Request.Path.StartsWithSegments(FacePrefix, StringComparison.OrdinalIgnoreCase);

    private static void Record(HttpContext context, ICurrentUser user, Guid clientId, IApiRequestLogSink sink, TimeProvider time, long started)
    {
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
        sink.Enqueue(new ApiRequestLogEntry(
            clientId,
            user.ActorType == ActorType.ApiKey ? user.ActorId : null,
            user.ActorType == ActorType.User ? user.ActorId : null,
            context.Request.Method,
            route,
            context.Response.StatusCode,
            (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            context.Request.ContentLength,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.ToString(),
            context.Items[ApiProblem.ErrorCodeItem] as string,
            CorrelationIdMiddleware.GetCorrelationId(context),
            time.GetUtcNow().UtcDateTime));
    }
}
