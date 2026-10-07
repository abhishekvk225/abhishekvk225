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

    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetRoleNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    Task SetRolesAsync(User user, IReadOnlyCollection<Role> roles, CancellationToken cancellationToken);

    void Add(User user);
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
