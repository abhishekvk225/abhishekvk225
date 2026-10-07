using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Identity;

namespace NexaVerify.Application.Identity;

public interface IRoleService
{
    Task<Result<IReadOnlyList<RoleDto>>> ListRolesAsync(CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<PermissionDto>>> ListPermissionsAsync(CancellationToken cancellationToken);

    Task<Result<RoleDto>> CreateRoleAsync(CreateRoleRequest request, CancellationToken cancellationToken);

    Task<Result<RoleDto>> UpdateRoleAsync(Guid id, UpdateRoleRequest request, CancellationToken cancellationToken);
}

/// <summary>Manages roles and their permissions. Roles are data; a caller can never grant a permission they do not hold themselves.</summary>
public sealed class RoleService : IRoleService
{
    private readonly IRoleRepository _roles;
    private readonly IPermissionResolver _permissions;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;

    public RoleService(IRoleRepository roles, IPermissionResolver permissions, ICurrentUser currentUser, IAuditService audit, IUnitOfWork unitOfWork)
    {
        _roles = roles;
        _permissions = permissions;
        _currentUser = currentUser;
        _audit = audit;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<IReadOnlyList<RoleDto>>> ListRolesAsync(CancellationToken cancellationToken)
    {
        var roles = await _roles.ListAsync(cancellationToken);
        var catalogue = (await _roles.ListPermissionsAsync(cancellationToken)).ToDictionary(p => p.Id, p => p.Key);
        return Result<IReadOnlyList<RoleDto>>.Success(roles.Select(r => ToDto(r, catalogue)).ToList());
    }

    public async Task<Result<IReadOnlyList<PermissionDto>>> ListPermissionsAsync(CancellationToken cancellationToken)
    {
        var permissions = await _roles.ListPermissionsAsync(cancellationToken);
        return Result<IReadOnlyList<PermissionDto>>.Success(permissions
            .OrderBy(p => p.Group, StringComparer.Ordinal).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new PermissionDto(p.Id, p.Key, p.Group, (PermissionScopeKind)(int)p.Scope, p.Description))
            .ToList());
    }

    public async Task<Result<RoleDto>> CreateRoleAsync(CreateRoleRequest request, CancellationToken cancellationToken)
    {
        var scope = Enum.Parse<RoleScope>(request.Scope);
        var normalized = request.Name.Trim().ToUpperInvariant();
        if (await _roles.NameExistsAsync(normalized, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Conflict, "A role with this name already exists.");
        }

        var role = Role.Create(request.Name, scope, isSystem: false, request.Description);
        var granted = await ApplyPermissionsAsync(role, request.Permissions, cancellationToken);
        if (granted.IsFailure)
        {
            return granted.Error!;
        }

        _roles.Add(role);
        _audit.Record(new AuditEntry(AuditActions.RoleCreated, nameof(Role), role.Id.ToString(), PlatformTenant.ClientId,
            NewValues: new { role.Name, Scope = scope.ToString(), Permissions = request.Permissions }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _permissions.Invalidate();
        return await ReloadAsync(role.Id, cancellationToken);
    }

    public async Task<Result<RoleDto>> UpdateRoleAsync(Guid id, UpdateRoleRequest request, CancellationToken cancellationToken)
    {
        var role = await _roles.GetByIdAsync(id, cancellationToken);
        if (role is null)
        {
            return Error.NotFound();
        }

        try
        {
            role.EnsureEditable();
        }
        catch (DomainException ex)
        {
            return Error.Conflict(ex.Code, ex.Message);
        }

        var catalogue = (await _roles.ListPermissionsAsync(cancellationToken)).ToDictionary(p => p.Id, p => p.Key);
        var before = role.Permissions.Select(p => catalogue[p.PermissionId]).OrderBy(k => k, StringComparer.Ordinal).ToList();

        role.Description = request.Description;
        foreach (var existing in role.Permissions.ToList())
        {
            role.RevokePermission(existing.PermissionId);
        }

        var granted = await ApplyPermissionsAsync(role, request.Permissions, cancellationToken);
        if (granted.IsFailure)
        {
            return granted.Error!;
        }

        _audit.Record(new AuditEntry(AuditActions.RoleUpdated, nameof(Role), role.Id.ToString(), PlatformTenant.ClientId,
            OldValues: new { Permissions = before }, NewValues: new { Permissions = request.Permissions.OrderBy(k => k, StringComparer.Ordinal).ToList() }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _permissions.Invalidate();
        return await ReloadAsync(role.Id, cancellationToken);
    }

    private async Task<Result> ApplyPermissionsAsync(Role role, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        var distinct = keys.Distinct(StringComparer.Ordinal).ToList();
        var found = await _roles.GetPermissionsByKeysAsync(distinct, cancellationToken);
        var unknown = distinct.Except(found.Select(p => p.Key), StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            return Error.Validation("Unknown permissions: " + string.Join(", ", unknown));
        }

        // Scope compatibility first (a client permission can never belong to a platform role), then the escalation guard.
        try
        {
            foreach (var permission in found)
            {
                role.GrantPermission(permission);
            }
        }
        catch (DomainException ex)
        {
            return Error.Validation(ex.Message);
        }

        // Privilege-escalation guard: you can only grant what you hold.
        var mine = await _permissions.GetPermissionsAsync(_currentUser.Roles, cancellationToken);
        var escalation = distinct.Where(k => !mine.Contains(k)).ToList();
        if (escalation.Count > 0)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "You cannot grant permissions you do not hold: " + string.Join(", ", escalation));
        }

        return Result.Success();
    }

    private async Task<Result<RoleDto>> ReloadAsync(Guid id, CancellationToken cancellationToken)
    {
        var role = await _roles.GetByIdAsync(id, cancellationToken);
        var catalogue = (await _roles.ListPermissionsAsync(cancellationToken)).ToDictionary(p => p.Id, p => p.Key);
        return role is null ? Error.NotFound() : ToDto(role, catalogue);
    }

    private static RoleDto ToDto(Role role, IReadOnlyDictionary<Guid, string> catalogue) =>
        new(role.Id, role.Name, role.Scope.ToString(), role.IsSystem, role.Description,
            role.Permissions.Select(p => catalogue.GetValueOrDefault(p.PermissionId, "?")).OrderBy(k => k, StringComparer.Ordinal).ToList());
}

public interface IPlatformUserService
{
    Task<Result<PagedResult<PlatformUserDto>>> ListAsync(PageRequest page, CancellationToken cancellationToken);

    Task<Result<PlatformUserDto>> CreateAsync(CreatePlatformUserRequest request, CancellationToken cancellationToken);

    Task<Result<PlatformUserDto>> UpdateAsync(Guid id, UpdatePlatformUserRequest request, CancellationToken cancellationToken);
}

/// <summary>Platform staff accounts. Only platform-scope roles can be assigned, and never more privilege than the caller holds.</summary>
public sealed class PlatformUserService : IPlatformUserService
{
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly IPermissionResolver _permissions;
    private readonly IPasswordHasher _hasher;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISessionValidator _sessions;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly TimeProvider _time;

    public PlatformUserService(
        IUserRepository users,
        IRoleRepository roles,
        IPermissionResolver permissions,
        IPasswordHasher hasher,
        ICurrentUser currentUser,
        IAuditService audit,
        IUnitOfWork unitOfWork,
        ISessionValidator sessions,
        IRefreshTokenRepository refreshTokens,
        TimeProvider time)
    {
        _users = users;
        _roles = roles;
        _permissions = permissions;
        _hasher = hasher;
        _currentUser = currentUser;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _sessions = sessions;
        _refreshTokens = refreshTokens;
        _time = time;
    }

    public async Task<Result<PagedResult<PlatformUserDto>>> ListAsync(PageRequest page, CancellationToken cancellationToken)
    {
        var request = page.Normalize();
        var (items, total) = await _users.ListAsync(platformUsers: true, request.Search, request.Skip, request.PageSize, cancellationToken);
        var roles = await _users.GetRoleNamesAsync(items.Select(u => u.Id).ToList(), cancellationToken);
        return new PagedResult<PlatformUserDto>(
            items.Select(u => ToDto(u, roles.GetValueOrDefault(u.Id, []))).ToList(), request.Page, request.PageSize, total);
    }

    public async Task<Result<PlatformUserDto>> CreateAsync(CreatePlatformUserRequest request, CancellationToken cancellationToken)
    {
        var normalized = User.Normalize(request.Email);
        if (await _users.EmailExistsAsync(normalized, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Conflict, "A user with this email already exists.");
        }

        var roles = await ResolveRolesAsync(request.Roles, cancellationToken);
        if (roles.IsFailure)
        {
            return roles.Error!;
        }

        var user = User.Create(request.Email, request.FullName, _hasher.Hash(request.TemporaryPassword), PlatformTenant.ClientId,
            isPlatformUser: true, mustChangePassword: true);
        _users.Add(user);
        await _users.SetRolesAsync(user, roles.Value, cancellationToken);
        _audit.Record(new AuditEntry(AuditActions.UserCreated, nameof(User), user.Id.ToString(), PlatformTenant.ClientId,
            NewValues: new { user.Email, user.FullName, Roles = request.Roles }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(user, roles.Value.Select(r => r.Name).ToList());
    }

    public async Task<Result<PlatformUserDto>> UpdateAsync(Guid id, UpdatePlatformUserRequest request, CancellationToken cancellationToken)
    {
        var user = await _users.GetByIdAsync(id, cancellationToken);
        if (user is null || !user.IsPlatformUser)
        {
            return Error.NotFound();
        }

        if (user.Id == _currentUser.ActorId && !request.IsActive)
        {
            return Error.Conflict(ErrorCodes.Conflict, "You cannot deactivate your own account.");
        }

        var roles = await ResolveRolesAsync(request.Roles, cancellationToken);
        if (roles.IsFailure)
        {
            return roles.Error!;
        }

        var before = new { user.FullName, Status = user.Status.ToString(), Roles = await _users.GetRoleNamesAsync(user.Id, cancellationToken) };
        user.FullName = request.FullName.Trim();
        var wasActive = user.Status == UserStatus.Active;
        if (request.IsActive)
        {
            user.Activate();
        }
        else
        {
            user.Deactivate();
        }

        await _users.SetRolesAsync(user, roles.Value, cancellationToken);
        user.RevokeSessions(); // role/status changes take effect immediately
        if (!request.IsActive)
        {
            foreach (var token in await _refreshTokens.GetActiveForUserAsync(user.Id, cancellationToken))
            {
                token.Revoke(_time.GetUtcNow().UtcDateTime, "user-deactivated");
            }
        }

        _audit.Record(new AuditEntry(request.IsActive || !wasActive ? AuditActions.UserUpdated : AuditActions.UserDeactivated,
            nameof(User), user.Id.ToString(), PlatformTenant.ClientId, OldValues: before,
            NewValues: new { user.FullName, Status = user.Status.ToString(), Roles = request.Roles }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _sessions.Invalidate(user.Id);
        return ToDto(user, roles.Value.Select(r => r.Name).ToList());
    }

    private async Task<Result<IReadOnlyCollection<Role>>> ResolveRolesAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        var distinct = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var roles = await _roles.GetByNamesAsync(distinct, cancellationToken);
        var missing = distinct.Except(roles.Select(r => r.Name), StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
        {
            return Error.Validation("Unknown roles: " + string.Join(", ", missing));
        }

        if (roles.Any(r => r.Scope != RoleScope.Platform))
        {
            return Error.Validation("Only platform roles can be assigned to platform users.");
        }

        var catalogue = (await _roles.ListPermissionsAsync(cancellationToken)).ToDictionary(p => p.Id, p => p.Key);
        var mine = await _permissions.GetPermissionsAsync(_currentUser.Roles, cancellationToken);
        var escalation = roles
            .SelectMany(r => r.Permissions.Select(p => catalogue[p.PermissionId]))
            .Where(k => !mine.Contains(k))
            .Distinct()
            .ToList();
        if (escalation.Count > 0)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "You cannot assign roles with permissions you do not hold: " + string.Join(", ", escalation));
        }

        return Result<IReadOnlyCollection<Role>>.Success(roles.ToList());
    }

    private static PlatformUserDto ToDto(User user, IReadOnlyList<string> roles) =>
        new(user.Id, user.Email, user.FullName, user.Status.ToString(), user.MustChangePassword, user.LastLoginAt, roles);
}
