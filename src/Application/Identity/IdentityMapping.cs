using NexaVerify.Contracts.Identity;
using NexaVerify.Domain.Identity;

namespace NexaVerify.Application.Identity;

internal static class IdentityMapping
{
    public static PermissionScope ToDomain(this PermissionScopeKind kind) => kind switch
    {
        PermissionScopeKind.Platform => PermissionScope.Platform,
        PermissionScopeKind.Client => PermissionScope.Client,
        PermissionScopeKind.Both => PermissionScope.Both,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static PermissionScopeKind ToKind(this PermissionScope scope) => scope switch
    {
        PermissionScope.Platform => PermissionScopeKind.Platform,
        PermissionScope.Client => PermissionScopeKind.Client,
        PermissionScope.Both => PermissionScopeKind.Both,
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    public static UserSummary ToSummary(this User user, IReadOnlyList<string> roles) =>
        new(user.Id, user.Email, user.FullName, user.IsPlatformUser, user.IsPlatformUser ? null : user.ClientId, roles);
}
