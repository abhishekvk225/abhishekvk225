using MudBlazor;

namespace NexaVerify.Web.Components;

public enum StatusKind
{
    Client,
    License,
    ApiKey,
    Outcome,
    User,
    Webhook,
    Delivery,
    Notification,
}

/// <summary>Visual treatment of a status: colour AND icon AND text, so colour is never the only signal.</summary>
public sealed record StatusStyle(string Label, Color Color, string Icon);

/// <summary>Maps domain status strings (as sent by the API) onto a visual style. Unknown values get a neutral style, never an exception.</summary>
public static class StatusMap
{
    private static readonly Dictionary<(StatusKind, string), StatusStyle> Styles = Build();

    public static StatusStyle Resolve(StatusKind kind, string? value)
    {
        var key = Normalize(value);
        if (Styles.TryGetValue((kind, key), out var style))
        {
            return style;
        }

        return new StatusStyle(Humanize(value), Color.Default, Icons.Material.Outlined.HelpOutline);
    }

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static string Humanize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        var spaced = System.Text.RegularExpressions.Regex.Replace(value.Replace('_', ' '), "(?<=[a-z])(?=[A-Z])", " ");
        return char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
    }

    private static Dictionary<(StatusKind, string), StatusStyle> Build()
    {
        var map = new Dictionary<(StatusKind, string), StatusStyle>();

        void Add(StatusKind kind, string key, string label, Color color, string icon) =>
            map[(kind, key)] = new StatusStyle(label, color, icon);

        const string ok = Icons.Material.Outlined.CheckCircle;
        const string pause = Icons.Material.Outlined.PauseCircle;
        const string block = Icons.Material.Outlined.Block;
        const string warn = Icons.Material.Outlined.WarningAmber;
        const string err = Icons.Material.Outlined.ErrorOutline;
        const string time = Icons.Material.Outlined.Schedule;

        Add(StatusKind.Client, "active", "Active", Color.Success, ok);
        Add(StatusKind.Client, "pending", "Pending", Color.Info, time);
        Add(StatusKind.Client, "pendingactivation", "Pending", Color.Info, time);
        Add(StatusKind.Client, "deleted", "Deleted", Color.Default, block);
        Add(StatusKind.Client, "suspended", "Suspended", Color.Warning, pause);
        Add(StatusKind.Client, "inactive", "Inactive", Color.Default, block);

        Add(StatusKind.License, "draft", "Draft", Color.Default, Icons.Material.Outlined.EditNote);
        Add(StatusKind.License, "active", "Active", Color.Success, ok);
        Add(StatusKind.License, "expiringsoon", "Expiring soon", Color.Warning, warn);
        Add(StatusKind.License, "exhausted", "Used up", Color.Error, err);
        Add(StatusKind.License, "expired", "Expired", Color.Error, time);
        Add(StatusKind.License, "suspended", "Suspended", Color.Warning, pause);
        Add(StatusKind.License, "cancelled", "Cancelled", Color.Default, block);
        Add(StatusKind.License, "inactive", "Inactive", Color.Default, block);
        Add(StatusKind.License, "revoked", "Revoked", Color.Error, block);

        Add(StatusKind.User, "active", "Active", Color.Success, ok);
        Add(StatusKind.User, "inactive", "Inactive", Color.Default, block);
        Add(StatusKind.User, "locked", "Locked", Color.Warning, pause);
        Add(StatusKind.User, "disabled", "Disabled", Color.Default, block);
        Add(StatusKind.User, "invited", "Invited", Color.Info, time);
        Add(StatusKind.User, "pendingactivation", "Invited", Color.Info, time);

        Add(StatusKind.ApiKey, "active", "Active", Color.Success, ok);
        Add(StatusKind.ApiKey, "expired", "Expired", Color.Warning, time);
        Add(StatusKind.ApiKey, "revoked", "Revoked", Color.Error, block);

        Add(StatusKind.Webhook, "active", "On", Color.Success, ok);
        Add(StatusKind.Webhook, "disabled", "Off", Color.Default, pause);

        Add(StatusKind.Delivery, "pending", "Waiting", Color.Info, time);
        Add(StatusKind.Delivery, "delivered", "Delivered", Color.Success, ok);
        Add(StatusKind.Delivery, "abandoned", "Gave up", Color.Error, err);

        Add(StatusKind.Notification, "info", "Info", Color.Info, Icons.Material.Outlined.Info);
        Add(StatusKind.Notification, "warning", "Warning", Color.Warning, warn);
        Add(StatusKind.Notification, "critical", "Urgent", Color.Error, err);
        Add(StatusKind.Notification, "error", "Urgent", Color.Error, err);

        Add(StatusKind.Outcome, "enrolled", "Enrolled", Color.Success, Icons.Material.Outlined.PersonAdd);
        Add(StatusKind.Outcome, "matched", "Match", Color.Success, ok);
        Add(StatusKind.Outcome, "nomatch", "No match", Color.Info, Icons.Material.Outlined.Cancel);
        Add(StatusKind.Outcome, "nofacedetected", "No face found", Color.Warning, Icons.Material.Outlined.FaceRetouchingOff);
        Add(StatusKind.Outcome, "multiplefaces", "Several faces", Color.Warning, Icons.Material.Outlined.Groups);
        Add(StatusKind.Outcome, "lowquality", "Photo quality too low", Color.Warning, warn);
        Add(StatusKind.Outcome, "providererror", "Service error", Color.Error, err);
        Add(StatusKind.Outcome, "rejected", "Rejected", Color.Error, block);

        return map;
    }
}
