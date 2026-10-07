namespace NexaVerify.Contracts.Identity;

public sealed record PermissionDto(Guid Id, string Key, string Group, PermissionScopeKind Scope, string Description);

public sealed record RoleDto(Guid Id, string Name, string Scope, bool IsSystem, string? Description, IReadOnlyList<string> Permissions);

public sealed record CreateRoleRequest(string Name, string Scope, string? Description, IReadOnlyList<string> Permissions);

public sealed record UpdateRoleRequest(string? Description, IReadOnlyList<string> Permissions);

public sealed record PlatformUserDto(Guid Id, string Email, string FullName, string Status, bool MustChangePassword, DateTime? LastLoginAt, IReadOnlyList<string> Roles);

public sealed record CreatePlatformUserRequest(string Email, string FullName, string TemporaryPassword, IReadOnlyList<string> Roles);

public sealed record UpdatePlatformUserRequest(string FullName, IReadOnlyList<string> Roles, bool IsActive);
