using System.Text.Json;

namespace NexaVerify.Contracts.Tenancy;

public static class SettingKeys
{
    public static class Face
    {
        public const string MatchThreshold = "face.matchThreshold";
        public const string MaxFacesPerImage = "face.maxFacesPerImage";
        public const string MinQuality = "face.minQuality";
        public const string RetainImages = "face.retainImages";
        public const string RetentionDays = "face.retentionDays";
        public const string IdentifyTopK = "face.identifyTopK";
    }

    public static class Api
    {
        public const string RateLimitPerMinute = "api.rateLimitPerMinute";
        public const string DailyQuota = "api.dailyQuota";
    }

    public static class Limits
    {
        public const string MaxProfiles = "limits.maxProfiles";
        public const string MaxApiKeys = "limits.maxApiKeys";
        public const string MaxUsers = "limits.maxUsers";
    }

    public static class Notify
    {
        public const string LowBalancePercent = "notify.lowBalancePercent";
        public const string ExpiryDaysBefore = "notify.expiryDaysBefore";
        public const string EmailEnabled = "notify.emailEnabled";
    }

    public static class Integration
    {
        public const string AllowedIps = "integration.allowedIps";
        public const string AllowedOrigins = "integration.allowedOrigins";
        public const string WebhooksEnabled = "webhook.enabled";
    }

    public static class Security
    {
        public const string PasswordMaxAgeDays = "security.passwordMaxAgeDays";
        public const string RequireMfa = "security.requireMfa";
    }
}

public sealed record SettingDto(
    string Key, string Group, string Type, JsonElement Value, JsonElement Default, decimal? Min, decimal? Max,
    string ManagedBy, bool Editable, bool IsOverridden, string Description);

public sealed record UpdateSettingsRequest(IReadOnlyDictionary<string, JsonElement> Values);
