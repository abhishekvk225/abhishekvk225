using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;

namespace NexaVerify.Api.Authorization;

/// <summary>Requires a permission key (e.g. <c>licenses.read</c>). New permissions need no new policy: policies are built on demand.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class HasPermissionAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "perm:";

    public HasPermissionAttribute(string permission)
        : base(PolicyPrefix + permission)
    {
        Permission = permission;
    }

    public string Permission { get; }
}

public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(string permission)
    {
        Permission = permission;
    }

    public string Permission { get; }
}

/// <summary>Builds <c>perm:*</c> policies dynamically, rejecting keys that are not in the code-side catalogue (typos fail loudly).</summary>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private static readonly HashSet<string> Known = Permissions.All.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        _fallback = new DefaultAuthorizationPolicyProvider(options);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(HasPermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            return _fallback.GetPolicyAsync(policyName);
        }

        var permission = policyName[HasPermissionAttribute.PolicyPrefix.Length..];
        if (!Known.Contains(permission))
        {
            throw new InvalidOperationException($"Unknown permission '{permission}' used in [HasPermission]. Add it to Contracts.Permissions.");
        }

        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permission))
            .Build();
        return Task.FromResult<AuthorizationPolicy?>(policy);
    }
}

/// <summary>
/// Grants a permission when the principal's roles (resolved through the cached role→permission map) include it AND the
/// permission's scope matches the principal's kind: platform permissions only for platform users, client permissions only
/// for client users or API keys. Principals that must change their password hold no permissions until they do.
/// </summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private static readonly Dictionary<string, PermissionScopeKind> Scopes = Permissions.All.ToDictionary(p => p.Key, p => p.Scope, StringComparer.Ordinal);

    private readonly ICurrentUser _user;
    private readonly IPermissionResolver _permissions;

    public PermissionAuthorizationHandler(ICurrentUser user, IPermissionResolver permissions)
    {
        _user = user;
        _permissions = permissions;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (!_user.IsAuthenticated || context.User.HasClaim(NexaClaims.MustChangePassword, "true"))
        {
            return;
        }

        var scope = Scopes[requirement.Permission];
        var allowedForPrincipal = _user.IsPlatformUser
            ? scope is PermissionScopeKind.Platform or PermissionScopeKind.Both
            : _user.ClientId is not null && scope is PermissionScopeKind.Client or PermissionScopeKind.Both;
        if (!allowedForPrincipal)
        {
            return;
        }

        // An API key holds exactly the scopes it was issued with; everyone else holds what their roles grant.
        IReadOnlySet<string> granted = _user.ActorType == ActorType.ApiKey
            ? context.User.FindAll(NexaClaims.Scope).Select(c => c.Value).ToHashSet(StringComparer.Ordinal)
            : await _permissions.GetPermissionsAsync(_user.Roles, CancellationToken.None);
        if (granted.Contains(requirement.Permission))
        {
            context.Succeed(requirement);
        }
    }
}
