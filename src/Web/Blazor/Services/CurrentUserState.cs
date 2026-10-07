namespace NexaVerify.Web.Services;

public enum Portal
{
    Admin,
    Client,
}

/// <summary>
/// Scoped holder for the signed-in user as the UI sees it. UI-1 uses <see cref="SignInDemo"/>; the BFF will populate it from the session.
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

    public void SignIn(string displayName, string email, string roleName, string? clientName, IEnumerable<string> permissions)
    {
        DisplayName = displayName;
        Email = email;
        RoleName = roleName;
        ClientName = clientName;
        _permissions = new HashSet<string>(permissions, StringComparer.Ordinal);
        IsSignedIn = true;
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

    /// <summary>Signs in a fake user for the portal when nobody is signed in (dev/stub only).</summary>
    public void SignInDemo(Portal portal)
    {
        if (IsSignedIn && (portal == Portal.Admin) == Has(WebPermissions.DashboardAdmin))
        {
            return;
        }

        if (portal == Portal.Admin)
        {
            SignIn("Priya Raman", "priya@nexaverify.example", "Super Admin", null, WebPermissions.SuperAdminDefaults);
        }
        else
        {
            SignIn("Alex Morgan", "alex@acme.example", "Client Admin", "Acme Corp", WebPermissions.ClientAdminDefaults);
        }
    }
}
