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

/// <summary>Emergency: revoke every active API key of a client (or one key) at once.</summary>
public sealed record EmergencyRevokeRequest(string Reason);

public sealed record EmergencyRevokeResultDto(int RevokedKeys, bool ApiAccessDisabled);

/// <summary>The client-wide kill switch: while disabled, no API key of the client is accepted (sign-in to the portal is unaffected).</summary>
public sealed record SetApiAccessRequest(bool Disabled, string Reason);

public sealed record ApiAccessDto(bool Disabled);

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

public sealed record WebhookEndpointDto(
    Guid Id, string Name, string Url, IReadOnlyList<string> Events, string Status, int FailureCount, DateTime? DisabledAt, string? DisabledReason,
    DateTime CreatedAt, string RowVersion);

/// <summary>The only response that contains the signing secret. Store it now: it cannot be shown again.</summary>
public sealed record CreatedWebhookDto(WebhookEndpointDto Endpoint, string Secret);

public sealed record CreateWebhookRequest(string Name, string Url, IReadOnlyList<string> Events);

public sealed record UpdateWebhookRequest(string Name, string Url, IReadOnlyList<string> Events, bool Enabled, string RowVersion);

public sealed record WebhookDeliveryDto(
    long Id, Guid EventId, string EventType, string Status, int Attempts, DateTime NextAttemptAt, int? LastStatusCode, string? LastError,
    DateTime CreatedAt, DateTime? DeliveredAt);

public sealed record WebhookEventDto(string Type, string Description);
