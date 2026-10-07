using NexaVerify.Domain.Auditing;
using NexaVerify.Domain.Identity;

namespace NexaVerify.Application.Persistence;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task<(IReadOnlyList<User> Items, int Total)> ListAsync(bool platformUsers, string? search, int skip, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetRoleNamesAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetRoleNamesByUserAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    Task<int> CountActivePlatformUsersInRoleAsync(string roleName, CancellationToken cancellationToken);

    Task<IReadOnlyList<User>> GetUsersWithRoleAsync(Guid roleId, CancellationToken cancellationToken);

    Task SetRolesAsync(User user, IReadOnlyCollection<Role> roles, CancellationToken cancellationToken);

    void Add(User user);
}

/// <summary>
/// Atomic failed-attempt accounting. The counter is incremented in the database BEFORE the password is checked, so N
/// parallel guesses consume N attempts (no verify-then-count race) and the lockout cannot be bypassed by concurrency.
/// </summary>
public interface ILoginThrottle
{
    /// <summary>Reserves one attempt. The attempt is allowed while the count stays within the limit; the next one locks the account.</summary>
    Task<AttemptState> ReserveAttemptAsync(Guid userId, DateTime now, int maxAttempts, TimeSpan lockoutDuration, CancellationToken cancellationToken);

    /// <summary>Resets the counter and any lockout (after a successful sign-in or a password reset).</summary>
    Task ClearAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed record AttemptState(bool Locked, int Attempts, DateTime? LockoutEnd);

/// <summary>Atomically claims a refresh token for rotation: exactly one of several concurrent callers wins.</summary>
public interface IRefreshTokenClaimer
{
    Task<bool> TryClaimAsync(Guid tokenId, DateTime now, CancellationToken cancellationToken);
}

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> FindByHashAsync(byte[] hash, CancellationToken cancellationToken);

    Task<IReadOnlyList<RefreshToken>> GetActiveForUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RefreshToken>> GetFamilyAsync(Guid familyId, CancellationToken cancellationToken);

    void Add(RefreshToken token);
}

public interface IPasswordResetTokenRepository
{
    Task<PasswordResetToken?> FindUsableAsync(Guid userId, byte[] hash, DateTime now, CancellationToken cancellationToken);

    Task<IReadOnlyList<PasswordResetToken>> GetUnusedForUserAsync(Guid userId, DateTime now, CancellationToken cancellationToken);

    void Add(PasswordResetToken token);
}

public interface ILoginHistoryRepository
{
    void Add(LoginHistory entry);
}

public interface IRoleRepository
{
    Task<IReadOnlyList<Role>> ListAsync(CancellationToken cancellationToken);

    Task<Role?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Role>> GetByNamesAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken);

    Task<bool> NameExistsAsync(string normalizedName, CancellationToken cancellationToken);

    Task<IReadOnlyList<Permission>> ListPermissionsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Permission>> GetPermissionsByKeysAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken);

    void Add(Role role);
}

public interface IAuditLogWriter
{
    void Add(AuditLog entry);
}

/// <summary>Resolves a user's effective permission keys from role names (cached; invalidated when RBAC data changes).</summary>
public interface IPermissionResolver
{
    Task<IReadOnlySet<string>> GetPermissionsAsync(IEnumerable<string> roleNames, CancellationToken cancellationToken);

    void Invalidate();
}

/// <summary>Cheap cached check used on every authenticated request: is this user's token still valid?</summary>
public interface ISessionValidator
{
    Task<bool> IsValidAsync(Guid userId, int securityVersion, CancellationToken cancellationToken);

    void Invalidate(Guid userId);
}

public interface IClientRepository
{
    Task<Domain.Tenancy.Client?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> CodeExistsAsync(string normalizedCode, CancellationToken cancellationToken);

    void Add(Domain.Tenancy.Client client);

    /// <summary>Makes the save fail with a concurrency conflict if the row changed since <paramref name="rowVersion"/> was read.</summary>
    void SetExpectedVersion(Domain.Tenancy.Client client, byte[] rowVersion);
}

public sealed record ClientListRow(Domain.Tenancy.Client Client, int UserCount);

public sealed record ClientUserRow(User User, Domain.Tenancy.ClientUser? Membership, string Role);

/// <summary>Read-side queries for the tenancy screens (projection-heavy, kept apart from the write repositories).</summary>
public interface IClientQueries
{
    Task<(IReadOnlyList<ClientListRow> Items, int Total)> ListClientsAsync(string? search, Domain.Tenancy.ClientStatus? status, int skip, int take, CancellationToken cancellationToken);

    Task<int> CountActiveUsersAsync(Guid clientId, CancellationToken cancellationToken);

    Task<(IReadOnlyList<ClientUserRow> Items, int Total)> ListUsersAsync(Guid clientId, string? search, int skip, int take, CancellationToken cancellationToken);

    Task<ClientUserRow?> GetUserAsync(Guid clientId, Guid userId, CancellationToken cancellationToken);

    Task<int> CountActiveUsersWithRoleAsync(Guid clientId, string roleName, CancellationToken cancellationToken);

    Task<(IReadOnlyList<AuditLog> Items, int Total)> ListAuditAsync(Guid clientId, DateTime? from, DateTime? to, string? action, int skip, int take, CancellationToken cancellationToken);

    Task<(IReadOnlyList<LoginHistory> Items, int Total)> ListLoginsAsync(Guid clientId, DateTime? from, DateTime? to, string? outcome, int skip, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<RefreshToken>> GetActiveRefreshTokensAsync(Guid clientId, CancellationToken cancellationToken);
}

public interface IClientSettingRepository
{
    Task<IReadOnlyList<Domain.Tenancy.ClientSetting>> GetAllAsync(Guid clientId, CancellationToken cancellationToken);

    void Add(Domain.Tenancy.ClientSetting setting);

    void Remove(Domain.Tenancy.ClientSetting setting);
}

public interface IClientMembershipRepository
{
    Task<Domain.Tenancy.ClientUser?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken);

    void Add(Domain.Tenancy.ClientUser membership);
}

/// <summary>Creates the client's wrapped data-encryption key (needs the master key, so it lives in infrastructure).</summary>
public interface IClientKeyProvisioner
{
    Task ProvisionAsync(Guid clientId, CancellationToken cancellationToken);
}
