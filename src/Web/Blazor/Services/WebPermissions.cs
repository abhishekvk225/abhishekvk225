namespace NexaVerify.Web.Services;

/// <summary>
/// Permission keys used to trim navigation. Mirrors the catalogue in docs/04 §3 (the API remains the only enforcement point).
/// Replace with <c>Contracts.Permissions</c> once that exists.
/// </summary>
public static class WebPermissions
{
    public const string ClientsRead = "clients.read";
    public const string ClientsCreate = "clients.create";
    public const string ClientsUpdate = "clients.update";
    public const string ClientsManageStatus = "clients.manage-status";
    public const string ClientsResetPassword = "clients.reset-password";
    public const string ClientsSettings = "clients.settings";

    public const string LicensesRead = "licenses.read";
    public const string LicensesCreate = "licenses.create";
    public const string LicensesUpdate = "licenses.update";
    public const string LicensesManageStatus = "licenses.manage-status";
    public const string LicensesRenew = "licenses.renew";
    public const string LicensesAdjust = "licenses.adjust";
    public const string LicensesCostRules = "licenses.cost-rules";

    public const string PlansManage = "plans.manage";
    public const string DashboardAdmin = "dashboard.admin";
    public const string DashboardClient = "dashboard.client";
    public const string ReportsRead = "reports.read";
    public const string AuditRead = "audit.read";
    public const string AuditReadClient = "audit.read.client";
    public const string SystemConfigure = "system.configure";
    public const string RolesManage = "roles.manage";
    public const string UsersPlatformManage = "users.platform-manage";
    public const string UsersManage = "users.manage";

    public const string ClientProfileRead = "client.profile.read";
    public const string ClientProfileUpdate = "client.profile.update";
    public const string LicenseRead = "license.read";
    public const string UsageRead = "usage.read";
    public const string ApiKeysRead = "apikeys.read";
    public const string ApiKeysManage = "apikeys.manage";
    public const string ApiLogsRead = "apilogs.read";
    public const string SettingsRecognition = "settings.recognition";
    public const string SettingsNotifications = "settings.notifications";
    public const string SettingsSecurity = "settings.security";
    public const string WebhooksManage = "webhooks.manage";
    public const string NotificationsRead = "notifications.read";

    public const string FacesEnroll = "faces.enroll";
    public const string FacesVerify = "faces.verify";
    public const string FacesIdentify = "faces.identify";
    public const string FacesDetect = "faces.detect";
    public const string FacesRead = "faces.read";
    public const string FacesManage = "faces.manage";
    public const string FacesErase = "faces.erase";
    public const string FacesHistory = "faces.history";

    /// <summary>Permissions of the default Super Admin role (docs/04 role matrix) - used only by the stub user.</summary>
    public static readonly IReadOnlyList<string> SuperAdminDefaults =
    [
        ClientsRead, ClientsCreate, ClientsUpdate, ClientsManageStatus, ClientsResetPassword, ClientsSettings,
        LicensesRead, LicensesCreate, LicensesUpdate, LicensesManageStatus, LicensesRenew, LicensesAdjust, LicensesCostRules,
        PlansManage, DashboardAdmin, ReportsRead, AuditRead, SystemConfigure, RolesManage, UsersPlatformManage,
    ];

    /// <summary>Permissions of the default Client Admin role - used only by the stub user.</summary>
    public static readonly IReadOnlyList<string> ClientAdminDefaults =
    [
        DashboardClient, ClientProfileRead, ClientProfileUpdate, LicenseRead, UsageRead, ApiKeysRead, ApiKeysManage, ApiLogsRead,
        SettingsRecognition, SettingsNotifications, SettingsSecurity, WebhooksManage, NotificationsRead, UsersManage, AuditReadClient,
        FacesEnroll, FacesVerify, FacesIdentify, FacesDetect, FacesRead, FacesManage, FacesErase, FacesHistory,
    ];
}
