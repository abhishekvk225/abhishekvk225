using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Identity;

/// <summary>A named bundle of permissions. System roles are immutable; new roles are data, not code.</summary>
public sealed class Role : AuditableEntity
{
    private readonly List<RolePermission> _permissions = [];

    private Role()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string NormalizedName { get; private set; } = string.Empty;

    public RoleScope Scope { get; private set; }

    public bool IsSystem { get; private set; }

    public string? Description { get; set; }

    public IReadOnlyCollection<RolePermission> Permissions => _permissions;

    public static Role Create(string name, RoleScope scope, bool isSystem, string? description)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 60)
        {
            throw new DomainException("ROLE_NAME_INVALID", "Role name is required (max 60 characters).");
        }

        return new Role
        {
            Name = name.Trim(),
            NormalizedName = name.Trim().ToUpperInvariant(),
            Scope = scope,
            IsSystem = isSystem,
            Description = description,
        };
    }

    public void EnsureEditable()
    {
        if (IsSystem)
        {
            throw new DomainException("ROLE_IMMUTABLE", "System roles cannot be modified.");
        }
    }

    public void GrantPermission(Permission permission)
    {
        if (!IsCompatible(permission.Scope))
        {
            throw new DomainException("ROLE_PERMISSION_SCOPE_MISMATCH",
                $"Permission '{permission.Key}' ({permission.Scope}) cannot be granted to a {Scope} role.");
        }

        if (_permissions.All(p => p.PermissionId != permission.Id))
        {
            _permissions.Add(new RolePermission { RoleId = Id, PermissionId = permission.Id });
        }
    }

    public void RevokePermission(Guid permissionId) => _permissions.RemoveAll(p => p.PermissionId == permissionId);

    private bool IsCompatible(PermissionScope permissionScope) => permissionScope switch
    {
        PermissionScope.Both => true,
        PermissionScope.Platform => Scope == RoleScope.Platform,
        PermissionScope.Client => Scope == RoleScope.Client,
        _ => false,
    };
}

public sealed class RolePermission
{
    public Guid RoleId { get; set; }

    public Guid PermissionId { get; set; }
}
