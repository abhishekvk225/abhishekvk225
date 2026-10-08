namespace NexaVerify.Contracts.Api;

public sealed record ApiKeyDto(
    Guid Id, string Name, string Prefix, IReadOnlyList<string> Scopes, string Status, string EffectiveStatus, DateTime? ExpiresAt,
    DateTime? LastUsedAt, string? LastUsedIp, int? RateLimitPerMinute, IReadOnlyList<string> AllowedIps, Guid? RotatedFromKeyId,
    DateTime CreatedAt, DateTime? RevokedAt, string? RevokedReason, string RowVersion);

/// <summary>The only response that ever contains the raw key. Store it now: it cannot be shown again.</summary>
public sealed record CreatedApiKeyDto(ApiKeyDto Key, string RawKey);

public sealed record CreateApiKeyRequest(string Name, IReadOnlyList<string> Scopes, DateTime? ExpiresAt, int? RateLimitPerMinute, IReadOnlyList<string>? AllowedIps);

public sealed record UpdateApiKeyRequest(string Name, IReadOnlyList<string> Scopes, DateTime? ExpiresAt, int? RateLimitPerMinute, IReadOnlyList<string>? AllowedIps, string RowVersion);

public sealed record RevokeApiKeyRequest(string Reason);

/// <summary>Issues a replacement key. The old key stops working after <c>GraceMinutes</c> (0 = immediately).</summary>
public sealed record RegenerateApiKeyRequest(int GraceMinutes);

public sealed record ApiScopeDto(string Key, string Description);

public sealed record ApiRequestLogDto(
    long Id, Guid? ApiKeyId, string Method, string Route, int StatusCode, int DurationMs, string? IpAddress, string? ErrorCode, string? CorrelationId, DateTime CreatedAt);

public sealed record ApiLogQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;

    public Guid? ApiKeyId { get; init; }

    /// <summary>Success (2xx), ClientError (4xx) or ServerError (5xx).</summary>
    public string? StatusClass { get; init; }

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }
}
