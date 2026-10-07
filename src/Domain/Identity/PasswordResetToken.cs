using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Identity;

/// <summary>Single-use, short-lived password-reset token (only its hash is stored).</summary>
public sealed class PasswordResetToken : Entity, ITenantOwned
{
    private PasswordResetToken()
    {
    }

    public Guid ClientId { get; set; }

    public Guid UserId { get; private set; }

    [AuditIgnore]
    public byte[] TokenHash { get; private set; } = [];

    public DateTime CreatedAt { get; private set; }

    public DateTime ExpiresAt { get; private set; }

    public DateTime? UsedAt { get; private set; }

    public static PasswordResetToken Issue(User user, byte[] tokenHash, DateTime now, TimeSpan lifetime) => new()
    {
        ClientId = user.ClientId,
        UserId = user.Id,
        TokenHash = tokenHash,
        CreatedAt = now,
        ExpiresAt = now.Add(lifetime),
    };

    public bool IsUsable(DateTime now) => UsedAt is null && ExpiresAt > now;

    public void MarkUsed(DateTime now) => UsedAt = now;
}
