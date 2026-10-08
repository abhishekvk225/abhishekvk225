using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Identity;

/// <summary>
/// A user's TOTP credential (RFC 6238). The shared secret is stored ONLY encrypted (never in plaintext, logs or audit values).
/// While <see cref="IsConfirmed"/> is false it is a pending enrolment that grants nothing.
/// </summary>
public sealed class UserMfa : Entity, ITenantOwned
{
    /// <summary>A pending enrolment is discarded after this many wrong confirmation codes.</summary>
    public const int MaxConfirmFailures = 5;

    private UserMfa()
    {
    }

    public Guid ClientId { get; set; }

    public Guid UserId { get; private set; }

    [AuditIgnore]
    public byte[] SecretEnc { get; private set; } = [];

    public bool IsConfirmed { get; private set; }

    /// <summary>The newest time step already accepted; a code for this or an older step is a replay and is refused.</summary>
    public long? LastUsedStep { get; private set; }

    public int ConfirmFailures { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? ConfirmedAt { get; private set; }

    public static UserMfa StartEnrolment(User user, byte[] secretEnc, DateTime now) => new()
    {
        ClientId = user.ClientId,
        UserId = user.Id,
        SecretEnc = secretEnc,
        CreatedAt = now,
    };

    /// <summary>Replaces the secret of a not-yet-confirmed enrolment (the user restarted the setup).</summary>
    public void Restart(byte[] secretEnc, DateTime now)
    {
        if (IsConfirmed)
        {
            throw new DomainException("MFA_ALREADY_ENABLED", "Two-factor authentication is already enabled.");
        }

        SecretEnc = secretEnc;
        ConfirmFailures = 0;
        LastUsedStep = null;
        CreatedAt = now;
    }

    /// <summary>Counts a wrong confirmation code; returns true when the pending enrolment is now spent and must be restarted.</summary>
    public bool RegisterConfirmFailure() => ++ConfirmFailures >= MaxConfirmFailures;

    public void Confirm(long step, DateTime now)
    {
        IsConfirmed = true;
        LastUsedStep = step;
        ConfirmedAt = now;
        ConfirmFailures = 0;
    }
}

/// <summary>A single-use recovery code. Only its SHA-256 is stored (the code is 50 random bits, shown once).</summary>
public sealed class MfaRecoveryCode : Entity, ITenantOwned
{
    private MfaRecoveryCode()
    {
    }

    public Guid ClientId { get; set; }

    public Guid UserId { get; private set; }

    [AuditIgnore]
    public byte[] CodeHash { get; private set; } = [];

    public DateTime CreatedAt { get; private set; }

    public DateTime? UsedAt { get; private set; }

    public static MfaRecoveryCode Issue(User user, byte[] codeHash, DateTime now) => new()
    {
        ClientId = user.ClientId,
        UserId = user.Id,
        CodeHash = codeHash,
        CreatedAt = now,
    };
}

/// <summary>
/// The short-lived (minutes), single-use ticket that stands between "password accepted" and "tokens issued". It is bound to the
/// user (not to an IP address), carries only a hash of its secret, and dies after a handful of wrong codes.
/// </summary>
public sealed class MfaChallenge : Entity, ITenantOwned
{
    private MfaChallenge()
    {
    }

    public Guid ClientId { get; set; }

    public Guid UserId { get; private set; }

    [AuditIgnore]
    public byte[] TokenHash { get; private set; } = [];

    public DateTime CreatedAt { get; private set; }

    public DateTime ExpiresAt { get; private set; }

    /// <summary>Wrong or right, every attempt is counted atomically in the database before the code is checked.</summary>
    public int Attempts { get; private set; }

    public DateTime? ConsumedAt { get; private set; }

    public static MfaChallenge Issue(User user, byte[] tokenHash, DateTime now, TimeSpan lifetime) => new()
    {
        ClientId = user.ClientId,
        UserId = user.Id,
        TokenHash = tokenHash,
        CreatedAt = now,
        ExpiresAt = now.Add(lifetime),
    };

    public bool IsLive(DateTime now, int maxAttempts) => ConsumedAt is null && ExpiresAt > now && Attempts < maxAttempts;
}
