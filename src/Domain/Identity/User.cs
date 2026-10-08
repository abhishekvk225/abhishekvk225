using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Identity;

/// <summary>
/// A person who can sign in. Platform staff belong to <see cref="PlatformTenant.ClientId"/>; client users to their client.
/// Credential state (lockout, security version) lives here so the rules are enforced in one place.
/// </summary>
public sealed class User : AuditableEntity, ITenantOwned
{
    public const int EmailMaxLength = 256;

    private User()
    {
    }

    public Guid ClientId { get; set; }

    public string Email { get; private set; } = string.Empty;

    public string NormalizedEmail { get; private set; } = string.Empty;

    [AuditIgnore]
    public string PasswordHash { get; private set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public string? PhoneNumber { get; set; }

    public UserStatus Status { get; private set; } = UserStatus.Active;

    public bool IsPlatformUser { get; private set; }

    public bool MustChangePassword { get; private set; }

    /// <summary>Bumped whenever sessions must be invalidated (password change, deactivation, role change).</summary>
    public int SecurityVersion { get; private set; } = 1;

    public int AccessFailedCount { get; private set; }

    public DateTime? LockoutEnd { get; private set; }

    public DateTime? LastLoginAt { get; private set; }

    public DateTime? LastPasswordChangedAt { get; private set; }

    public bool TwoFactorEnabled { get; private set; }

    public static string Normalize(string email) => email.Trim().ToUpperInvariant();

    public static User Create(string email, string fullName, string passwordHash, Guid clientId, bool isPlatformUser, bool mustChangePassword)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > EmailMaxLength)
        {
            throw new DomainException("USER_EMAIL_INVALID", "Email is required and must be at most 256 characters.");
        }

        if (!isPlatformUser && (clientId == Guid.Empty || clientId == PlatformTenant.ClientId))
        {
            throw new DomainException("USER_CLIENT_INVALID", "A client user must belong to a real client.");
        }

        return new User
        {
            ClientId = isPlatformUser ? PlatformTenant.ClientId : clientId,
            Email = email.Trim(),
            NormalizedEmail = Normalize(email),
            FullName = fullName.Trim(),
            PasswordHash = passwordHash,
            IsPlatformUser = isPlatformUser,
            MustChangePassword = mustChangePassword,
        };
    }

    public bool IsLockedOut(DateTime now) => LockoutEnd is { } end && end > now;

    /// <summary>
    /// Whether the account may hold sessions at all. A lockout deliberately does NOT affect this: it only blocks password
    /// sign-in, so an attacker who locks a victim out cannot also kill the victim's live sessions or block their password reset.
    /// </summary>
    public bool CanSignIn() => Status == UserStatus.Active && IsActive;

    /// <summary>Failed-attempt counters are maintained atomically in the database (ILoginThrottle), never in memory.</summary>
    public void RegisterSuccessfulLogin(DateTime now) => LastLoginAt = now;

    /// <summary>Replaces the stored hash with a stronger one for the same password. Does not end other sessions.</summary>
    public void UpgradeHash(string passwordHash) => PasswordHash = passwordHash;

    public void SetPassword(string passwordHash, DateTime now, bool mustChangePassword)
    {
        PasswordHash = passwordHash;
        MustChangePassword = mustChangePassword;
        LastPasswordChangedAt = now;
        AccessFailedCount = 0;
        LockoutEnd = null;
        RevokeSessions();
    }

    /// <summary>Invalidates every access token issued so far (they carry the old version).</summary>
    public void RevokeSessions() => SecurityVersion++;

    public void Deactivate()
    {
        Status = UserStatus.Inactive;
        RevokeSessions();
    }

    public void Activate() => Status = UserStatus.Active;

    /// <summary>Turns two-factor sign-in on. Sessions issued before are ended so every live token belongs to a second-factor login.</summary>
    public void EnableTwoFactor()
    {
        TwoFactorEnabled = true;
        RevokeSessions();
    }

    /// <summary>Turns two-factor sign-in off (only ever done by another administrator) and ends every session.</summary>
    public void DisableTwoFactor()
    {
        TwoFactorEnabled = false;
        RevokeSessions();
    }
}
