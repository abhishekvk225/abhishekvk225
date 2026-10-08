using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Api;

/// <summary>
/// One API call, for the client's usage screens and for support. Route TEMPLATE only (never the raw URL), no bodies, headers or
/// query strings, so a log row can never carry an identifier or personal data from the request.
/// </summary>
public sealed class ApiRequestLog : ITenantOwned
{
    public long Id { get; private set; }

    public Guid ClientId { get; set; }

    public Guid? ApiKeyId { get; private set; }

    public Guid? UserId { get; private set; }

    public string Method { get; private set; } = string.Empty;

    public string RouteTemplate { get; private set; } = string.Empty;

    public int StatusCode { get; private set; }

    public int DurationMs { get; private set; }

    public long? RequestBytes { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? CorrelationId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public static ApiRequestLog Create(
        Guid clientId, Guid? apiKeyId, Guid? userId, string method, string routeTemplate, int statusCode, int durationMs, long? requestBytes,
        string? ip, string? userAgent, string? errorCode, string? correlationId, DateTime now) =>
        new()
        {
            ClientId = clientId,
            ApiKeyId = apiKeyId,
            UserId = userId,
            Method = Truncate(method, 8)!,
            RouteTemplate = Truncate(routeTemplate, 200)!,
            StatusCode = statusCode,
            DurationMs = durationMs,
            RequestBytes = requestBytes,
            IpAddress = Truncate(ip, 45),
            UserAgent = Truncate(userAgent, 200),
            ErrorCode = Truncate(errorCode, 60),
            CorrelationId = Truncate(correlationId, 64),
            CreatedAt = now,
        };

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
