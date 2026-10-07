using System.Net;
using System.Text.Json;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Tenancy;

namespace NexaVerify.Application.Tenancy;

public enum SettingType
{
    Int,
    Decimal,
    Bool,
    StringList,
}

public enum SettingManager
{
    /// <summary>Only the platform operator can change it (limits, quotas).</summary>
    Platform,

    /// <summary>The client can change it within the bounds below (the platform operator can too).</summary>
    Client,
}

public sealed record SettingDefinition(
    string Key,
    string Group,
    SettingType Type,
    SettingManager ManagedBy,
    string Description,
    object Default,
    decimal? Min = null,
    decimal? Max = null,
    string? ClientEditPermission = null);

/// <summary>
/// Code-side source of truth for per-client settings: type, default, bounds and who may edit. New settings need no
/// migration — add a definition (values are stored as JSON per client, only when overridden).
/// </summary>
public static class SettingCatalog
{
    private const int MaxListItems = 50;

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new(SettingKeys.Face.MatchThreshold, "Recognition", SettingType.Decimal, SettingManager.Client, "Minimum similarity (0–1) for two faces to count as a match.", 0.60m, 0.30m, 0.99m, Permissions.Settings.Recognition),
        new(SettingKeys.Face.MaxFacesPerImage, "Recognition", SettingType.Int, SettingManager.Client, "Maximum faces allowed in one submitted image.", 1, 1, 5, Permissions.Settings.Recognition),
        new(SettingKeys.Face.MinQuality, "Recognition", SettingType.Decimal, SettingManager.Client, "Minimum image quality score (0–1) accepted for enrolment.", 0.50m, 0m, 1m, Permissions.Settings.Recognition),
        new(SettingKeys.Face.RetainImages, "Recognition", SettingType.Bool, SettingManager.Client, "Keep submitted images (encrypted). Off by default for privacy.", false, null, null, Permissions.Settings.Recognition),
        new(SettingKeys.Face.RetentionDays, "Recognition", SettingType.Int, SettingManager.Client, "Days before registered faces are automatically erased.", 365, 1, 3650, Permissions.Settings.Recognition),
        new(SettingKeys.Face.IdentifyTopK, "Recognition", SettingType.Int, SettingManager.Client, "How many best candidates an identification returns.", 5, 1, 20, Permissions.Settings.Recognition),

        new(SettingKeys.Api.RateLimitPerMinute, "API", SettingType.Int, SettingManager.Platform, "API requests allowed per minute per key.", 60, 1, 100000),
        new(SettingKeys.Api.DailyQuota, "API", SettingType.Int, SettingManager.Platform, "API requests allowed per day for the whole client.", 10000, 1, 100000000),

        new(SettingKeys.Limits.MaxProfiles, "Limits", SettingType.Int, SettingManager.Platform, "Maximum registered people (face profiles).", 10000, 1, 100000000),
        new(SettingKeys.Limits.MaxApiKeys, "Limits", SettingType.Int, SettingManager.Platform, "Maximum active API keys.", 5, 1, 100),
        new(SettingKeys.Limits.MaxUsers, "Limits", SettingType.Int, SettingManager.Platform, "Maximum active users.", 10, 1, 1000),

        new(SettingKeys.Notify.LowBalancePercent, "Notifications", SettingType.Int, SettingManager.Client, "Warn when remaining credits fall below this percentage.", 20, 1, 90, Permissions.Settings.Notifications),
        new(SettingKeys.Notify.ExpiryDaysBefore, "Notifications", SettingType.Int, SettingManager.Client, "Warn this many days before a license expires.", 30, 1, 365, Permissions.Settings.Notifications),
        new(SettingKeys.Notify.EmailEnabled, "Notifications", SettingType.Bool, SettingManager.Client, "Send alert emails in addition to in-app notifications.", true, null, null, Permissions.Settings.Notifications),

        new(SettingKeys.Integration.AllowedIps, "Integration", SettingType.StringList, SettingManager.Client, "IP addresses or CIDR ranges allowed to use the API keys (empty = any).", Array.Empty<string>(), null, null, Permissions.Settings.Security),
        new(SettingKeys.Integration.AllowedOrigins, "Integration", SettingType.StringList, SettingManager.Client, "Browser origins (https://host) allowed to call the API directly.", Array.Empty<string>(), null, null, Permissions.Settings.Security),
        new(SettingKeys.Integration.WebhooksEnabled, "Integration", SettingType.Bool, SettingManager.Platform, "Whether the client may use webhooks.", true),

        new(SettingKeys.Security.PasswordMaxAgeDays, "Security", SettingType.Int, SettingManager.Client, "Force a password change after this many days (0 = never).", 0, 0, 365, Permissions.Settings.Security),
        new(SettingKeys.Security.RequireMfa, "Security", SettingType.Bool, SettingManager.Client, "Require multi-factor authentication for all users.", false, null, null, Permissions.Settings.Security),
    ];

    private static readonly Dictionary<string, SettingDefinition> ByKey = All.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static SettingDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    public static JsonElement ToJson(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>Validates a client-supplied JSON value against the definition; returns the normalised JSON or an error message.</summary>
    public static (string? Json, string? Error) Normalize(SettingDefinition definition, JsonElement value)
    {
        switch (definition.Type)
        {
            case SettingType.Bool:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? (value.GetRawText(), null)
                    : (null, "Must be true or false.");

            case SettingType.Int:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var integer))
                {
                    return (null, "Must be a whole number.");
                }

                return InRange(definition, integer) is { } intError
                    ? (null, intError)
                    : (integer.ToString(System.Globalization.CultureInfo.InvariantCulture), null);

            case SettingType.Decimal:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
                {
                    return (null, "Must be a number.");
                }

                return InRange(definition, number) is { } rangeError
                    ? (null, rangeError)
                    : (number.ToString(System.Globalization.CultureInfo.InvariantCulture), null);

            case SettingType.StringList:
                if (value.ValueKind != JsonValueKind.Array)
                {
                    return (null, "Must be a list.");
                }

                var items = new List<string>();
                foreach (var element in value.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String || element.GetString() is not { Length: > 0 and <= 200 } text)
                    {
                        return (null, "Every entry must be a non-empty text value.");
                    }

                    items.Add(text.Trim());
                }

                if (items.Count > MaxListItems)
                {
                    return (null, $"At most {MaxListItems} entries are allowed.");
                }

                var itemError = items.Select(i => ValidateListItem(definition.Key, i)).FirstOrDefault(e => e is not null);
                return itemError is not null
                    ? (null, itemError)
                    : (JsonSerializer.Serialize(items.Distinct(StringComparer.OrdinalIgnoreCase)), null);

            default:
                return (null, "Unsupported setting type.");
        }
    }

    /// <summary>Semantic comparison of a normalised value with the definition's default (0.6 equals 0.60; list order is irrelevant).</summary>
    public static bool IsDefault(SettingDefinition definition, string json)
    {
        var value = JsonDocument.Parse(json).RootElement;
        var def = ToJson(definition.Default);
        return definition.Type switch
        {
            SettingType.Bool => value.GetBoolean() == def.GetBoolean(),
            SettingType.Int or SettingType.Decimal => value.GetDecimal() == def.GetDecimal(),
            SettingType.StringList => value.EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(def.EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static string? InRange(SettingDefinition d, decimal value) =>
        (d.Min is { } min && value < min) || (d.Max is { } max && value > max)
            ? $"Must be between {d.Min} and {d.Max}."
            : null;

    private static string? ValidateListItem(string key, string item)
    {
        if (key == SettingKeys.Integration.AllowedIps)
        {
            var parts = item.Split('/');
            var valid = parts.Length <= 2 && IPAddress.TryParse(parts[0], out var address)
                && (parts.Length == 1 || (int.TryParse(parts[1], out var prefix) && prefix >= 0 && prefix <= (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)));
            return valid ? null : $"'{item}' is not a valid IP address or CIDR range.";
        }

        if (key == SettingKeys.Integration.AllowedOrigins)
        {
            return Uri.TryCreate(item, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.AbsolutePath == "/" && !item.EndsWith('/')
                ? null
                : $"'{item}' must be an https origin such as https://app.example.com.";
        }

        return null;
    }
}
