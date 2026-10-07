using Serilog.Context;
using NexaVerify.Contracts.Common;

namespace NexaVerify.Api.Middleware;

/// <summary>
/// Assigns (or accepts a well-formed) correlation id, echoes it on the response and pushes it into every log line.
/// Client-supplied ids are length/charset-limited so they cannot be used for log injection.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    private const string ItemKey = "__CorrelationId";
    private const int MaxLength = 64;

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public static string GetCorrelationId(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string id ? id : context.TraceIdentifier;

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[HttpHeaderNames.CorrelationId].FirstOrDefault();
        var correlationId = IsAcceptable(supplied) ? supplied! : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HttpHeaderNames.CorrelationId] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }

    private static bool IsAcceptable(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
