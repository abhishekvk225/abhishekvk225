using System.Security.Claims;
using NexaVerify.Web.Security;

namespace NexaVerify.Web.Services;

public enum Portal
{
    Admin,
    Client,
}

/// <summary>
/// Scoped view of the signed-in user for components (display name, role, permissions to trim menus and actions).
/// Filled from the authenticated principal by the layouts; it holds no tokens and is never the source of truth for access.
/// </summary>
public sealed class CurrentUserState
{
    private HashSet<string> _permissions = new(StringComparer.Ordinal);

    public string DisplayName { get; private set; } = "Guest";

    public string Email { get; private set; } = string.Empty;

    public string RoleName { get; private set; } = string.Empty;

    public string? ClientName { get; private set; }

    public bool IsSignedIn { get; private set; }

    public IReadOnlyCollection<string> Permissions => _permissions;

    public bool Has(string permission) => _permissions.Contains(permission);

    public bool HasAny(params string[] permissions) => permissions.Any(_permissions.Contains);

    public void SignIn(string displayName, string email, string roleName, string? clientName, IEnumerable<string> permissions)
    {
        DisplayName = displayName;
        Email = email;
        RoleName = roleName;
        ClientName = clientName;
        _permissions = new HashSet<string>(permissions, StringComparer.Ordinal);
        IsSignedIn = true;
    }

    /// <summary>Loads the user from the portal principal (claims built from the server-side session).</summary>
    public void Load(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            SignOut();
            return;
        }

        SignIn(
            principal.FindFirst(PortalClaims.DisplayName)?.Value ?? principal.Identity.Name ?? "Signed in",
            principal.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty,
            principal.FindFirst(PortalClaims.RoleName)?.Value ?? string.Empty,
            principal.FindFirst(PortalClaims.ClientName)?.Value,
            principal.FindAll(PortalClaims.Permission).Select(c => c.Value));
    }

    public void SignOut()
    {
        DisplayName = "Guest";
        Email = string.Empty;
        RoleName = string.Empty;
        ClientName = null;
        _permissions = new HashSet<string>(StringComparer.Ordinal);
        IsSignedIn = false;
    }
}
