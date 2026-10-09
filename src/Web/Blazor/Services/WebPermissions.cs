using NexaVerify.Contracts.Identity;

namespace NexaVerify.Web.Services;

/// <summary>
/// Short names for the permission catalogue in <c>Contracts.Identity.Permissions</c>, used to trim navigation and hide actions.
/// Values are aliases of the contract constants so they cannot drift (the API remains the only enforcement point).
/// </summary>
public static class WebPermissions
{
    public const string ClientsRead = Permissions.Clients.Read;
    public const string ClientsCreate = Permissions.Clients.Create;
    public const string ClientsUpdate = Permissions.Clients.Update;
    public const string ClientsManageStatus = Permissions.Clients.ManageStatus;
    public const string ClientsResetPassword = Permissions.Clients.ResetPassword;
    public const string ClientsSettings = Permissions.Clients.Settings;

    public const string LicensesRead = Permissions.Licenses.Read;
    public const string LicensesCreate = Permissions.Licenses.Create;
    public const string LicensesUpdate = Permissions.Licenses.Update;
    public const string LicensesManageStatus = Permissions.Licenses.ManageStatus;
    public const string LicensesRenew = Permissions.Licenses.Renew;
    public const string LicensesAdjust = Permissions.Licenses.Adjust;
    public const string LicensesCostRules = Permissions.Licenses.CostRules;
    public const string LicensesVerifyLedger = Permissions.Licenses.VerifyLedger;

    public const string PlansManage = Permissions.Plans.Manage;
    public const string DashboardAdmin = Permissions.Dashboard.Admin;
    public const string DashboardClient = Permissions.Dashboard.Client;
    public const string ReportsRead = Permissions.Reports.Read;
    public const string AuditRead = Permissions.Audit.Read;
    public const string AuditReadClient = Permissions.Audit.ReadClient;
    public const string SystemConfigure = Permissions.System.Configure;
    public const string RolesManage = Permissions.RolesAdmin.Manage;
    public const string UsersPlatformManage = Permissions.Users.PlatformManage;
    public const string UsersManage = Permissions.Users.Manage;

    public const string ClientProfileRead = Permissions.ClientProfile.Read;
    public const string ClientProfileUpdate = Permissions.ClientProfile.Update;
    public const string LicenseRead = Permissions.LicenseView.Read;
    public const string UsageRead = Permissions.Usage.Read;
    public const string ApiKeysRead = Permissions.ApiKeys.Read;
    public const string ApiKeysManage = Permissions.ApiKeys.Manage;
    public const string ApiLogsRead = Permissions.ApiLogs.Read;
    public const string SettingsRecognition = Permissions.Settings.Recognition;
    public const string SettingsNotifications = Permissions.Settings.Notifications;
    public const string SettingsSecurity = Permissions.Settings.Security;
    public const string WebhooksManage = Permissions.Webhooks.Manage;
    public const string NotificationsRead = Permissions.Notifications.Read;

    public const string FacesEnroll = Permissions.Faces.Enroll;
    public const string FacesVerify = Permissions.Faces.Verify;
    public const string FacesIdentify = Permissions.Faces.Identify;
    public const string FacesDetect = Permissions.Faces.Detect;
    public const string FacesRead = Permissions.Faces.Read;
    public const string FacesManage = Permissions.Faces.Manage;
    public const string FacesErase = Permissions.Faces.Erase;
    public const string FacesHistory = Permissions.Faces.History;

    // Billing (M12). Not in Contracts.Identity.Permissions yet; the keys are the API's.
    public const string BillingRead = BillingPermissions.Read;
    public const string BillingManage = BillingPermissions.Manage;
    public const string BillingPacksManage = BillingPermissions.PacksManage;
    public const string BillingOrdersRead = BillingPermissions.OrdersRead;
    public const string BillingRefund = BillingPermissions.Refund;

    /// <summary>Permissions of the default Super Admin role (used by tests and the design stubs).</summary>
    public static IReadOnlyList<string> SuperAdminDefaults => SystemRoles.PermissionsFor(SystemRoles.SuperAdmin);

    /// <summary>Permissions of the default Client Admin role (used by tests and the design stubs).</summary>
    public static IReadOnlyList<string> ClientAdminDefaults => SystemRoles.PermissionsFor(SystemRoles.ClientAdmin);
}
