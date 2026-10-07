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

    public bool CanSignIn(DateTime now) => Status == UserStatus.Active && IsActive && !IsLockedOut(now);

    /// <summary>Counts a failed attempt; returns true when this attempt locked the account.</summary>
    public bool RegisterFailedLogin(DateTime now, int maxFailures, TimeSpan lockoutDuration)
    {
        // an elapsed lockout resets the counter so the next window starts clean
        if (LockoutEnd is { } end && end <= now)
        {
            AccessFailedCount = 0;
            LockoutEnd = null;
        }

        AccessFailedCount++;
        if (AccessFailedCount >= maxFailures)
        {
            LockoutEnd = now.Add(lockoutDuration);
            AccessFailedCount = 0;
            return true;
        }

        return false;
    }

    public void RegisterSuccessfulLogin(DateTime now)
    {
        AccessFailedCount = 0;
        LockoutEnd = null;
        LastLoginAt = now;
    }

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
}
