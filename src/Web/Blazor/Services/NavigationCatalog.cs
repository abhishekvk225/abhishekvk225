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
            new("Clients", "admin/clients", Icons.Material.Outlined.Business, WebPermissions.ClientsRead, Implemented: false),
        ]),
        new("Licensing",
        [
            new("Licenses", "admin/licenses", Icons.Material.Outlined.VpnKey, WebPermissions.LicensesRead, Implemented: false),
            new("Plans", "admin/plans", Icons.Material.Outlined.Inventory2, WebPermissions.PlansManage, Implemented: false),
            new("Cost rules", "admin/cost-rules", Icons.Material.Outlined.Toll, WebPermissions.LicensesCostRules, Implemented: false),
        ]),
        new("Insights",
        [
            new("Reports", "admin/reports", Icons.Material.Outlined.Assessment, WebPermissions.ReportsRead, Implemented: false),
            new("Audit logs", "admin/audit", Icons.Material.Outlined.History, WebPermissions.AuditRead, Implemented: false),
        ]),
        new("System",
        [
            new("Settings", "admin/settings", Icons.Material.Outlined.Settings, WebPermissions.SystemConfigure, Implemented: false),
            new("Roles & permissions", "admin/roles", Icons.Material.Outlined.AdminPanelSettings, WebPermissions.RolesManage, Implemented: false),
            new("Platform users", "admin/platform-users", Icons.Material.Outlined.Group, WebPermissions.UsersPlatformManage, Implemented: false),
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
            new("Enroll", "client/enroll", Icons.Material.Outlined.Face, WebPermissions.FacesEnroll, Implemented: false),
            new("Verify", "client/verify", Icons.Material.Outlined.VerifiedUser, WebPermissions.FacesVerify, Implemented: false),
            new("Identify", "client/identify", Icons.Material.Outlined.PersonSearch, WebPermissions.FacesIdentify, Implemented: false),
            new("Profiles", "client/profiles", Icons.Material.Outlined.Badge, WebPermissions.FacesRead, Implemented: false),
            new("History", "client/history", Icons.Material.Outlined.History, WebPermissions.FacesHistory, Implemented: false),
        ]),
        new("Developer",
        [
            new("API keys", "client/api-keys", Icons.Material.Outlined.Key, WebPermissions.ApiKeysRead, Implemented: false),
            new("API logs", "client/api-logs", Icons.Material.Outlined.Api, WebPermissions.ApiLogsRead, Implemented: false),
            new("Webhooks", "client/webhooks", Icons.Material.Outlined.Webhook, WebPermissions.WebhooksManage, Implemented: false),
        ]),
        new("Account",
        [
            new("License & usage", "client/license", Icons.Material.Outlined.Toll, WebPermissions.LicenseRead, Implemented: false),
            new("Users", "client/users", Icons.Material.Outlined.Group, WebPermissions.UsersManage, Implemented: false),
            new("Settings", "client/settings", Icons.Material.Outlined.Settings, WebPermissions.ClientProfileRead, Implemented: false),
            new("Activity", "client/activity", Icons.Material.Outlined.Timeline, WebPermissions.AuditReadClient, Implemented: false),
            new("Notifications", "client/notifications", Icons.Material.Outlined.Notifications, WebPermissions.NotificationsRead, Implemented: false),
        ]),
    ];

    /// <summary>Returns the groups the user may see; empty groups are dropped. Convenience only - the API enforces access.</summary>
    public static IReadOnlyList<NavGroup> Trim(IEnumerable<NavGroup> groups, Func<string, bool> hasPermission) =>
        groups
            .Select(g => new NavGroup(g.Title, g.Items.Where(i => i.RequiredPermission is null || hasPermission(i.RequiredPermission)).ToList()))
            .Where(g => g.Items.Count > 0)
            .ToList();
}
