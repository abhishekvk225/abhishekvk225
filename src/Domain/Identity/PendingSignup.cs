using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Identity;

/// <summary>
/// A self-service sign-up that has not been email-verified yet. It belongs to no tenant (the client does not exist until the
/// address is proven). Only hashes are stored: the password hash and the SHA-256 of the emailed token. At most one live
/// (unconsumed) row exists per email; a repeated sign-up replaces the previous one.
/// </summary>
public sealed class PendingSignup : Entity
{
    public const int CompanyNameMaxLength = 150;
    public const int FullNameMaxLength = 150;

    private PendingSignup()
    {
    }

    public string CompanyName { get; private set; } = string.Empty;

    public string FullName { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    public string NormalizedEmail { get; private set; } = string.Empty;

    [AuditIgnore]
    public string PasswordHash { get; private set; } = string.Empty;

    [AuditIgnore]
    public byte[] TokenHash { get; private set; } = [];

    public DateTime CreatedAt { get; private set; }

    public DateTime ExpiresAt { get; private set; }

    public DateTime? ConsumedAt { get; private set; }

    /// <summary>How many times the verification email was re-sent for this sign-up.</summary>
    public int ResendCount { get; private set; }

    public const int MaxResends = 3;

    /// <summary>Issues a fresh token (the old link stops working) and restarts the validity period. False when the sign-up is not live or has used its resends.</summary>
    public bool Reissue(byte[] tokenHash, DateTime now, TimeSpan lifetime)
    {
        if (!IsLive(now) || ResendCount >= MaxResends)
        {
            return false;
        }

        TokenHash = tokenHash;
        ExpiresAt = now.Add(lifetime);
        ResendCount++;
        return true;
    }

    public static PendingSignup Issue(string companyName, string fullName, string email, string passwordHash, byte[] tokenHash, DateTime now, TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(companyName) || companyName.Trim().Length > CompanyNameMaxLength)
        {
            throw new DomainException("SIGNUP_COMPANY_INVALID", "A company name of at most 150 characters is required.");
        }

        if (string.IsNullOrWhiteSpace(fullName) || fullName.Trim().Length > FullNameMaxLength)
        {
            throw new DomainException("SIGNUP_NAME_INVALID", "A name of at most 150 characters is required.");
        }

        if (string.IsNullOrWhiteSpace(email) || email.Length > User.EmailMaxLength)
        {
            throw new DomainException("SIGNUP_EMAIL_INVALID", "A valid email address is required.");
        }

        if (lifetime <= TimeSpan.Zero)
        {
            throw new DomainException("SIGNUP_LIFETIME_INVALID", "The verification period must be positive.");
        }

        return new PendingSignup
        {
            CompanyName = companyName.Trim(),
            FullName = fullName.Trim(),
            Email = email.Trim(),
            NormalizedEmail = User.Normalize(email),
            PasswordHash = passwordHash,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now.Add(lifetime),
        };
    }

    /// <summary>Still waiting for verification: not consumed and not expired.</summary>
    public bool IsLive(DateTime now) => ConsumedAt is null && ExpiresAt > now;
}
