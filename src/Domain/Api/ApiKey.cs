using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Api;

public enum ApiKeyStatus
{
    Active = 0,
    Revoked,
}

/// <summary>
/// A credential for a client's integration. Only a SHA-256 hash of the key is stored (the key is 256-bit random, so a fast hash is
/// correct); the raw key exists once, in the response that created it. Scopes are permission keys the key may use.
/// </summary>
public sealed class ApiKey : AuditableEntity, ITenantOwned
{
    public const int MaxScopes = 20;

    private ApiKey()
    {
    }

    public Guid ClientId { get; set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Public identifier shown in the portal and used to find the key (e.g. <c>nxv_live_a1b2c3d4</c>).</summary>
    public string KeyPrefix { get; private set; } = string.Empty;

    public byte[] KeyHash { get; private set; } = [];

    /// <summary>Comma-separated permission keys.</summary>
    public string Scopes { get; private set; } = string.Empty;

    public ApiKeyStatus Status { get; private set; }

    public DateTime? ExpiresAt { get; private set; }

    public DateTime? LastUsedAt { get; set; }

    public string? LastUsedIp { get; set; }

    /// <summary>Overrides the client's rate limit for this key.</summary>
    public int? RateLimitPerMinute { get; private set; }

    /// <summary>Comma-separated IP addresses / CIDR ranges this key may be used from; empty = any.</summary>
    public string AllowedIps { get; private set; } = string.Empty;

    public Guid? RotatedFromKeyId { get; private set; }

    public DateTime? RevokedAt { get; private set; }

    public Guid? RevokedBy { get; private set; }

    public string? RevokedReason { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<string> ScopeList => Scopes.Length == 0 ? [] : Scopes.Split(',');

    public IReadOnlyList<string> AllowedIpList => AllowedIps.Length == 0 ? [] : AllowedIps.Split(',');

    public static ApiKey Create(
        Guid clientId, string name, string prefix, byte[] hash, IReadOnlyCollection<string> scopes, DateTime? expiresAt,
        int? rateLimitPerMinute, IReadOnlyCollection<string> allowedIps, DateTime now, Guid? rotatedFrom = null)
    {
        var key = new ApiKey
        {
            ClientId = clientId,
            KeyPrefix = prefix,
            KeyHash = hash,
            Status = ApiKeyStatus.Active,
            RotatedFromKeyId = rotatedFrom,
        };
        key.Apply(name, scopes, expiresAt, rateLimitPerMinute, allowedIps, now);
        return key;
    }

    public void Update(string name, IReadOnlyCollection<string> scopes, DateTime? expiresAt, int? rateLimitPerMinute, IReadOnlyCollection<string> allowedIps, DateTime now)
    {
        if (Status == ApiKeyStatus.Revoked)
        {
            throw new DomainException("APIKEY_REVOKED", "A revoked key cannot be changed.");
        }

        Apply(name, scopes, expiresAt, rateLimitPerMinute, allowedIps, now);
    }

    public void Revoke(string reason, Guid? by, DateTime now)
    {
        if (Status == ApiKeyStatus.Revoked)
        {
            throw new DomainException("APIKEY_REVOKED", "This key is already revoked.");
        }

        Status = ApiKeyStatus.Revoked;
        RevokedAt = now;
        RevokedBy = by;
        RevokedReason = reason.Trim();
    }

    /// <summary>Shortens the key's life (used to retire the old key of a rotation after a grace period).</summary>
    public void ExpireAt(DateTime at)
    {
        if (ExpiresAt is null || at < ExpiresAt)
        {
            ExpiresAt = at;
        }
    }

    public bool IsExpired(DateTime now) => ExpiresAt is { } end && end <= now;

    public bool IsUsable(DateTime now) => Status == ApiKeyStatus.Active && !IsExpired(now);

    private void Apply(string name, IReadOnlyCollection<string> scopes, DateTime? expiresAt, int? rateLimitPerMinute, IReadOnlyCollection<string> allowedIps, DateTime now)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > 100)
        {
            throw new DomainException("APIKEY_NAME_INVALID", "Name must be 1–100 characters.");
        }

        if (scopes.Count == 0 || scopes.Count > MaxScopes)
        {
            throw new DomainException("APIKEY_SCOPES_INVALID", $"Choose between 1 and {MaxScopes} scopes.");
        }

        if (expiresAt is { } end && end <= now)
        {
            throw new DomainException("APIKEY_EXPIRY_INVALID", "The expiry must be in the future.");
        }

        if (rateLimitPerMinute is < 1 or > 100_000)
        {
            throw new DomainException("APIKEY_RATE_INVALID", "The rate limit must be between 1 and 100000 per minute.");
        }

        Name = trimmed;
        Scopes = string.Join(',', scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        ExpiresAt = expiresAt;
        RateLimitPerMinute = rateLimitPerMinute;
        AllowedIps = string.Join(',', allowedIps.Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
