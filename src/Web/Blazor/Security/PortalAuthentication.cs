using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NexaVerify.Web.Security;

/// <summary>Builds the portal principal from the server-side session. The cookie itself carries only the session id.</summary>
public static class PortalPrincipalFactory
{
    public const string AuthenticationType = "nv.session";

    /// <summary>The principal stored in the cookie ticket: nothing but the opaque session id.</summary>
    public static ClaimsPrincipal ForCookie(string sessionId) =>
        new(new ClaimsIdentity([new Claim(PortalClaims.SessionId, sessionId)], AuthenticationType));

    public static ClaimsPrincipal Create(PortalSession session)
    {
        var claims = new List<Claim>
        {
            new(PortalClaims.SessionId, session.Id),
            new(ClaimTypes.NameIdentifier, session.UserId.ToString()),
            new(ClaimTypes.Name, session.Email),
            new(PortalClaims.DisplayName, session.FullName),
            new(PortalClaims.Portal, session.Portal),
            new(PortalClaims.RoleName, string.Join(", ", session.Roles)),
        };
        claims.AddRange(session.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(session.Permissions.Select(p => new Claim(PortalClaims.Permission, p)));
        if (!string.IsNullOrEmpty(session.ClientName))
        {
            claims.Add(new Claim(PortalClaims.ClientName, session.ClientName));
        }

        if (session.MustChangePassword)
        {
            claims.Add(new Claim(PortalClaims.MustChangePassword, "true"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType, ClaimTypes.Name, ClaimTypes.Role));
    }
}

/// <summary>Cookie validation: the ticket is only as good as the server-side session behind it.</summary>
public static class SessionCookieEvents
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var sessionId = context.Principal?.FindFirst(PortalClaims.SessionId)?.Value;
        var store = context.HttpContext.RequestServices.GetRequiredService<ISessionStore>();
        var session = string.IsNullOrEmpty(sessionId) ? null : await store.GetAsync(sessionId, context.HttpContext.RequestAborted);
        if (session is null)
        {
            // Expired (idle/absolute), signed out elsewhere, or refresh token rejected: the cookie is dead.
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        if (!IsBackgroundRequest(context.Request))
        {
            await store.TouchAsync(session.Id, context.HttpContext.RequestAborted);
        }

        context.ReplacePrincipal(PortalPrincipalFactory.Create(session));
    }

    // Static assets and the SignalR transport are not user activity.
    private static bool IsBackgroundRequest(HttpRequest request) =>
        request.Path.StartsWithSegments("/_framework") || request.Path.StartsWithSegments("/_content") || request.Path.StartsWithSegments("/js")
        || request.Path.Value?.EndsWith(".css", StringComparison.OrdinalIgnoreCase) == true || request.Path.Value?.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) == true;
}

public static class Policies
{
    public const string PlatformPortal = "PlatformPortal";
    public const string ClientPortal = "ClientPortal";

    /// <summary>Signed in, even while a forced password change is pending (the change-password page).</summary>
    public const string SignedIn = "SignedIn";

    public const string PermissionPrefix = "perm:";

    public static string Permission(string permission) => PermissionPrefix + permission;
}

/// <summary>Requires a portal ("admin"/"client") and, unless <see cref="AllowPendingPasswordChange"/>, no pending forced password change.</summary>
public sealed class PortalRequirement(string? portal, string? permission, bool allowPendingPasswordChange) : IAuthorizationRequirement
{
    public string? Portal { get; } = portal;

    public string? Permission { get; } = permission;

    public bool AllowPendingPasswordChange { get; } = allowPendingPasswordChange;
}

public sealed class PortalRequirementHandler : AuthorizationHandler<PortalRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PortalRequirement requirement)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return Task.CompletedTask;
        }

        if (!requirement.AllowPendingPasswordChange && user.HasClaim(PortalClaims.MustChangePassword, "true"))
        {
            return Task.CompletedTask;
        }

        if (requirement.Portal is not null && !user.HasClaim(PortalClaims.Portal, requirement.Portal))
        {
            return Task.CompletedTask;
        }

        if (requirement.Permission is not null && !user.HasClaim(PortalClaims.Permission, requirement.Permission))
        {
            return Task.CompletedTask;
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>Resolves <c>perm:{key}</c> policy names, so a page declares <c>[Authorize(Policy = "perm:clients.read")]</c>.</summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(Policies.PermissionPrefix, StringComparison.Ordinal))
        {
            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PortalRequirement(null, policyName[Policies.PermissionPrefix.Length..], false))
                .Build();
        }

        return await base.GetPolicyAsync(policyName);
    }
}

/// <summary>
/// Keeps Blazor's authentication state honest during a long-lived circuit: every 30 seconds the session is looked up again, so an
/// idle timeout, a sign-out in another tab or a rejected refresh token takes effect without a page load.
/// </summary>
public sealed class SessionAuthenticationStateProvider(ILoggerFactory loggerFactory, ISessionStore store)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        var sessionId = authenticationState.User.FindFirst(PortalClaims.SessionId)?.Value;
        return !string.IsNullOrEmpty(sessionId) && await store.GetAsync(sessionId, cancellationToken) is not null;
    }
}

/// <summary>While a forced password change is pending, every page request goes to the change-password page.</summary>
public sealed class ForcePasswordChangeMiddleware(RequestDelegate next)
{
    private static readonly string[] AllowedPrefixes =
    [
        "/change-password", "/auth/", "/login", "/_blazor", "/_framework", "/_content", "/js", "/app.css", "/favicon", "/error", "/not-found", "/forbidden",
    ];

    public Task InvokeAsync(HttpContext context)
    {
        if (context.User.HasClaim(PortalClaims.MustChangePassword, "true")
            && HttpMethods.IsGet(context.Request.Method)
            && !AllowedPrefixes.Any(p => context.Request.Path.Value!.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.Redirect("/change-password");
            return Task.CompletedTask;
        }

        return next(context);
    }
}
