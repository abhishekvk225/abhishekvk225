using MudBlazor;

namespace NexaVerify.Web.Services;

/// <summary>Sidebar definitions for both portals (docs/06 §1). Items without a page yet are flagged not implemented.</summary>
public static class NavigationCatalog
{
    public static IReadOnlyList<NavGroup> Admin { get; } =
    [
        new("Overview",
        [
            new("Dashboard", "admin", Icons.Material.Outlined.Dashboard, WebPermissions.DashboardAdmin, Exact: true),
        ]),
        new("Clients",
        [
            new("Clients", "admin/clients", Icons.Material.Outlined.Business, WebPermissions.ClientsRead),
        ]),
        new("Licensing",
        [
            new("Licenses", "admin/licenses", Icons.Material.Outlined.VpnKey, WebPermissions.LicensesRead),
            new("Plans", "admin/plans", Icons.Material.Outlined.Inventory2, WebPermissions.LicensesRead),
            new("Cost rules", "admin/cost-rules", Icons.Material.Outlined.Toll, WebPermissions.LicensesRead),
        ]),
        new("Insights",
        [
            new("Reports", "admin/reports", Icons.Material.Outlined.Assessment, WebPermissions.ReportsRead),
            new("Audit logs", "admin/audit", Icons.Material.Outlined.History, WebPermissions.AuditRead),
        ]),
        new("System",
        [
            new("Settings", "admin/settings", Icons.Material.Outlined.Settings, WebPermissions.SystemConfigure, Implemented: false),
            new("Roles & permissions", "admin/roles", Icons.Material.Outlined.AdminPanelSettings, WebPermissions.RolesManage),
            new("Platform users", "admin/platform-users", Icons.Material.Outlined.Group, WebPermissions.UsersPlatformManage),
            new("Health", "admin/health", Icons.Material.Outlined.MonitorHeart, WebPermissions.SystemConfigure, Implemented: false),
        ]),
    ];

    public static IReadOnlyList<NavGroup> Client { get; } =
    [
        new("Overview",
        [
            new("Dashboard", "client", Icons.Material.Outlined.Dashboard, WebPermissions.DashboardClient, Exact: true),
        ]),
        new("Recognition",
        [
            new("Register", "client/enroll", Icons.Material.Outlined.Face, WebPermissions.FacesEnroll),
            new("Check a person", "client/verify", Icons.Material.Outlined.VerifiedUser, WebPermissions.FacesVerify),
            new("Find a person", "client/identify", Icons.Material.Outlined.PersonSearch, WebPermissions.FacesIdentify),
            new("People", "client/profiles", Icons.Material.Outlined.Badge, WebPermissions.FacesRead),
            new("History", "client/history", Icons.Material.Outlined.History, WebPermissions.FacesHistory),
        ]),
        new("Developer",
        [
            new("API keys", "client/api-keys", Icons.Material.Outlined.Key, WebPermissions.ApiKeysRead),
            new("API logs", "client/api-logs", Icons.Material.Outlined.Api, WebPermissions.ApiKeysRead),
            new("Webhooks", "client/webhooks", Icons.Material.Outlined.Webhook, WebPermissions.WebhooksManage),
            new("API guide", "client/api-docs", Icons.Material.Outlined.MenuBook, null),
        ]),
        new("Account",
        [
            new("License & usage", "client/license", Icons.Material.Outlined.Toll, WebPermissions.LicenseRead),
            new("People with access", "client/users", Icons.Material.Outlined.Group, WebPermissions.UsersManage),
            new("Settings", "client/settings", Icons.Material.Outlined.Settings, WebPermissions.ClientProfileRead),
            new("Activity", "client/activity", Icons.Material.Outlined.Timeline, WebPermissions.AuditReadClient),
            new("Notifications", "client/notifications", Icons.Material.Outlined.Notifications, WebPermissions.NotificationsRead),
        ]),
    ];

    /// <summary>Returns the groups the user may see; empty groups are dropped. Convenience only - the API enforces access.</summary>
    public static IReadOnlyList<NavGroup> Trim(IEnumerable<NavGroup> groups, Func<string, bool> hasPermission) =>
        groups
            .Select(g => new NavGroup(g.Title, g.Items.Where(i => i.RequiredPermission is null || hasPermission(i.RequiredPermission)).ToList()))
            .Where(g => g.Items.Count > 0)
            .ToList();
}
