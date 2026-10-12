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
        new("Billing",
        [
            new("Credit packs", "admin/billing/packs", Icons.Material.Outlined.Sell, WebPermissions.BillingPacksManage),
            new("Orders", "admin/billing/orders", Icons.Material.Outlined.ReceiptLong, WebPermissions.BillingOrdersRead),
        ]),
        new("Verification",
        [
            new("Operations", "admin/verification", Icons.Material.Outlined.QueryStats, WebPermissions.VerificationAdminRead, Exact: true),
            new("Modules", "admin/verification/modules", Icons.Material.Outlined.Extension, WebPermissions.VerificationAdminRead),
            new("Investigation", "admin/verification/requests", Icons.Material.Outlined.ManageSearch, WebPermissions.VerificationAdminRead),
            new("Audit", "admin/verification/audit", Icons.Material.Outlined.History, WebPermissions.AuditRead),
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
        new("Document verification",
        [
            new("Overview", "client/verification", Icons.Material.Outlined.FactCheck, WebPermissions.VerificationRead, Exact: true),
            new("Bank statement", "client/verification/bank-statement", Icons.Material.Outlined.AccountBalance, WebPermissions.VerificationBankStatementSubmit),
            new("PAN", "client/verification/pan", Icons.Material.Outlined.CreditCard, WebPermissions.VerificationPanSubmit),
            new("Passport", "client/verification/passport", Icons.Material.Outlined.Flight, WebPermissions.VerificationPassportSubmit),
            new("Verification history", "client/verification/history", Icons.Material.Outlined.History, WebPermissions.VerificationRead),
        ]),
        new("Developer",
        [
            new("API keys", "client/api-keys", Icons.Material.Outlined.Key, WebPermissions.ApiKeysRead),
            new("API logs", "client/api-logs", Icons.Material.Outlined.Api, WebPermissions.ApiKeysRead),
            new("Webhooks", "client/webhooks", Icons.Material.Outlined.Webhook, WebPermissions.WebhooksManage),
            new("API guide", "client/api-docs", Icons.Material.Outlined.MenuBook, null),
        ]),
        new("Billing",
        [
            new("Buy credits", "client/billing/buy", Icons.Material.Outlined.ShoppingCart, WebPermissions.BillingManage),
            new("Orders & invoices", "client/billing/orders", Icons.Material.Outlined.ReceiptLong, WebPermissions.BillingRead),
            new("Billing details", "client/billing/profile", Icons.Material.Outlined.Business, WebPermissions.BillingRead),
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
