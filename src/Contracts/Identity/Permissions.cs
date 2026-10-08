namespace NexaVerify.Contracts.Identity;

public enum PermissionScopeKind
{
    Platform = 0,
    Client,
    Both,
}

public sealed record PermissionDefinition(string Key, string Group, PermissionScopeKind Scope, string Description);

/// <summary>
/// The permission catalogue (docs/04 §3). Code is the source of truth: keys are synced into the database at startup.
/// Adding a capability = one constant here + a role mapping.
/// </summary>
public static class Permissions
{
    public static class Clients
    {
        public const string Read = "clients.read";
        public const string Create = "clients.create";
        public const string Update = "clients.update";
        public const string ManageStatus = "clients.manage-status";
        public const string ResetPassword = "clients.reset-password";
        public const string Settings = "clients.settings";
    }

    public static class Licenses
    {
        public const string Read = "licenses.read";
        public const string Create = "licenses.create";
        public const string Update = "licenses.update";
        public const string ManageStatus = "licenses.manage-status";
        public const string Renew = "licenses.renew";
        public const string Adjust = "licenses.adjust";
        public const string CostRules = "licenses.cost-rules";
        public const string VerifyLedger = "licenses.verify-ledger";
        public const string ApproveAdjust = "licenses.approve-adjust";
    }

    public static class Plans
    {
        public const string Manage = "plans.manage";
    }

    public static class Dashboard
    {
        public const string Admin = "dashboard.admin";
        public const string Client = "dashboard.client";
    }

    public static class Reports
    {
        public const string Read = "reports.read";
    }

    public static class Audit
    {
        public const string Read = "audit.read";
        public const string ReadClient = "audit.read.client";
    }

    public static class System
    {
        public const string Configure = "system.configure";
    }

    public static class RolesAdmin
    {
        public const string Manage = "roles.manage";
    }

    public static class Users
    {
        public const string PlatformManage = "users.platform-manage";
        public const string Manage = "users.manage";
        public const string MfaReset = "users.mfa-reset";
    }

    public static class ClientProfile
    {
        public const string Read = "client.profile.read";
        public const string Update = "client.profile.update";
    }

    public static class LicenseView
    {
        public const string Read = "license.read";
    }

    public static class Usage
    {
        public const string Read = "usage.read";
    }

    public static class ApiKeys
    {
        public const string Read = "apikeys.read";
        public const string Manage = "apikeys.manage";
    }

    public static class Emergency
    {
        public const string RevokeApiAccess = "apikeys.emergency-revoke";
    }

    public static class ApiLogs
    {
        public const string Read = "apilogs.read";
    }

    public static class Settings
    {
        public const string Recognition = "settings.recognition";
        public const string Notifications = "settings.notifications";
        public const string Security = "settings.security";
    }

    public static class Webhooks
    {
        public const string Manage = "webhooks.manage";
    }

    public static class Notifications
    {
        public const string Read = "notifications.read";
    }

    public static class Faces
    {
        public const string Enroll = "faces.enroll";
        public const string Verify = "faces.verify";
        public const string Identify = "faces.identify";
        public const string Detect = "faces.detect";
        public const string Read = "faces.read";
        public const string Manage = "faces.manage";
        public const string Erase = "faces.erase";
        public const string History = "faces.history";
    }

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(Clients.Read, "Clients", PermissionScopeKind.Platform, "View clients"),
        new(Clients.Create, "Clients", PermissionScopeKind.Platform, "Create clients"),
        new(Clients.Update, "Clients", PermissionScopeKind.Platform, "Edit client details"),
        new(Clients.ManageStatus, "Clients", PermissionScopeKind.Platform, "Activate, deactivate and suspend clients"),
        new(Clients.ResetPassword, "Clients", PermissionScopeKind.Platform, "Reset a client user's password"),
        new(Clients.Settings, "Clients", PermissionScopeKind.Platform, "Configure client-specific platform settings"),

        new(Licenses.Read, "Licenses", PermissionScopeKind.Platform, "View licenses and ledgers"),
        new(Licenses.Create, "Licenses", PermissionScopeKind.Platform, "Generate and assign licenses"),
        new(Licenses.Update, "Licenses", PermissionScopeKind.Platform, "Edit license details"),
        new(Licenses.ManageStatus, "Licenses", PermissionScopeKind.Platform, "Activate, deactivate, suspend and revoke licenses"),
        new(Licenses.Renew, "Licenses", PermissionScopeKind.Platform, "Renew licenses"),
        new(Licenses.Adjust, "Licenses", PermissionScopeKind.Platform, "Adjust license credits"),
        new(Licenses.CostRules, "Licenses", PermissionScopeKind.Platform, "Configure credit costs per operation"),
        new(Licenses.VerifyLedger, "Licenses", PermissionScopeKind.Platform, "Run a tamper check over the credit ledgers"),
        new(Licenses.ApproveAdjust, "Licenses", PermissionScopeKind.Platform, "Approve or reject large credit adjustments requested by someone else"),
        new(Plans.Manage, "Licenses", PermissionScopeKind.Platform, "Manage subscription plans"),

        new(Dashboard.Admin, "Dashboard", PermissionScopeKind.Platform, "View the admin dashboard"),
        new(Reports.Read, "Reports", PermissionScopeKind.Platform, "View and export platform reports"),
        new(Audit.Read, "Audit", PermissionScopeKind.Platform, "View the global audit trail"),
        new(System.Configure, "System", PermissionScopeKind.Platform, "Configure platform settings and view system health"),
        new(RolesAdmin.Manage, "Access control", PermissionScopeKind.Platform, "Manage roles and permissions"),
        new(Users.PlatformManage, "Access control", PermissionScopeKind.Platform, "Manage platform staff accounts"),
        new(Users.MfaReset, "Access control", PermissionScopeKind.Platform, "Reset another user's two-factor authentication (lost device)"),
        new(Emergency.RevokeApiAccess, "Emergency", PermissionScopeKind.Platform, "Revoke API keys of a client and switch its API access off"),

        new(Dashboard.Client, "Dashboard", PermissionScopeKind.Client, "View the client dashboard"),
        new(ClientProfile.Read, "Account", PermissionScopeKind.Client, "View company profile"),
        new(ClientProfile.Update, "Account", PermissionScopeKind.Client, "Edit company profile"),
        new(LicenseView.Read, "Account", PermissionScopeKind.Client, "View own license and credit history"),
        new(Users.Manage, "Account", PermissionScopeKind.Client, "Manage the client's users"),
        new(ApiKeys.Read, "Developer", PermissionScopeKind.Client, "View API keys"),
        new(ApiKeys.Manage, "Developer", PermissionScopeKind.Client, "Create, regenerate and revoke API keys"),
        new(ApiLogs.Read, "Developer", PermissionScopeKind.Client, "View API request logs"),
        new(Webhooks.Manage, "Developer", PermissionScopeKind.Client, "Manage webhooks"),
        new(Settings.Recognition, "Settings", PermissionScopeKind.Client, "Configure face recognition settings"),
        new(Settings.Notifications, "Settings", PermissionScopeKind.Client, "Configure notifications"),
        new(Settings.Security, "Settings", PermissionScopeKind.Client, "Configure security settings"),
        new(Audit.ReadClient, "Audit", PermissionScopeKind.Client, "View own activity and login history"),
        new(Faces.Enroll, "Face recognition", PermissionScopeKind.Client, "Register faces"),
        new(Faces.Verify, "Face recognition", PermissionScopeKind.Client, "Verify a face (1:1)"),
        new(Faces.Identify, "Face recognition", PermissionScopeKind.Client, "Identify a face (1:N)"),
        new(Faces.Detect, "Face recognition", PermissionScopeKind.Client, "Detect faces in an image"),
        new(Faces.Read, "Face recognition", PermissionScopeKind.Client, "View face profiles"),
        new(Faces.Manage, "Face recognition", PermissionScopeKind.Client, "Edit face profiles"),
        new(Faces.Erase, "Face recognition", PermissionScopeKind.Client, "Erase face profiles"),
        new(Faces.History, "Face recognition", PermissionScopeKind.Client, "View recognition history"),

        new(Usage.Read, "Usage", PermissionScopeKind.Both, "View usage statistics"),
        new(Notifications.Read, "Notifications", PermissionScopeKind.Both, "View notifications"),
    ];
}

/// <summary>Names of the built-in roles and their default permission sets.</summary>
public static class SystemRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string ClientAdmin = "ClientAdmin";
    public const string ClientUser = "ClientUser";

    public static IReadOnlyList<string> ClientUserPermissions { get; } =
    [
        Permissions.Dashboard.Client,
        Permissions.LicenseView.Read,
        Permissions.Usage.Read,
        Permissions.Notifications.Read,
        Permissions.Faces.Verify,
        Permissions.Faces.Identify,
        Permissions.Faces.Read,
        Permissions.Faces.History,
    ];

    public static IReadOnlyList<string> PermissionsFor(string role) => role switch
    {
        SuperAdmin => Permissions.All.Where(p => p.Scope != PermissionScopeKind.Client).Select(p => p.Key).ToList(),
        ClientAdmin => Permissions.All.Where(p => p.Scope != PermissionScopeKind.Platform).Select(p => p.Key).ToList(),
        ClientUser => ClientUserPermissions,
        _ => [],
    };
}
