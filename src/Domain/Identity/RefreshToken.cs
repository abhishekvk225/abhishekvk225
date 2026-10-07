using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Identity;

/// <summary>Opaque refresh token (only its SHA-256 is stored). Rotated on every use; reuse of a rotated token revokes the family.</summary>
public sealed class RefreshToken : Entity, ITenantOwned
{
    private RefreshToken()
    {
    }

    public Guid ClientId { get; set; }

    public Guid UserId { get; private set; }

    [AuditIgnore]
    public byte[] TokenHash { get; private set; } = [];

    public Guid FamilyId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime ExpiresAt { get; private set; }

    /// <summary>Hard cap for the whole family regardless of sliding renewals.</summary>
    public DateTime AbsoluteExpiresAt { get; private set; }

    public DateTime? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    public Guid? ReplacedByTokenId { get; private set; }

    public string? CreatedByIp { get; private set; }

    public string? UserAgent { get; private set; }

    public static RefreshToken Issue(User user, byte[] tokenHash, Guid familyId, DateTime now, TimeSpan lifetime, DateTime absoluteExpiry, string? ip, string? userAgent)
    {
        var expires = now.Add(lifetime);
        return new RefreshToken
        {
            ClientId = user.ClientId,
            UserId = user.Id,
            TokenHash = tokenHash,
            FamilyId = familyId,
            CreatedAt = now,
            ExpiresAt = expires < absoluteExpiry ? expires : absoluteExpiry,
            AbsoluteExpiresAt = absoluteExpiry,
            CreatedByIp = ip,
            UserAgent = userAgent is { Length: > 300 } ? userAgent[..300] : userAgent,
        };
    }

    public bool IsRevoked => RevokedAt is not null;

    public bool IsExpired(DateTime now) => ExpiresAt <= now;

    public bool IsUsable(DateTime now) => !IsRevoked && !IsExpired(now);

    public void Revoke(DateTime now, string reason, Guid? replacedBy = null)
    {
        if (IsRevoked)
        {
            return;
        }

        RevokedAt = now;
        RevokedReason = reason;
        ReplacedByTokenId = replacedBy;
    }
}
