namespace NexaVerify.Web.Components;

public enum LicenseHealthLevel
{
    Healthy,
    Warning,
    Critical,
}

/// <summary>
/// Colour state of the licence gauge (docs/06 §2), defined precisely:
/// <list type="bullet">
/// <item>Critical (red): credits left &lt; 10 % of total, OR fewer than 7 days to expiry (or already expired / nothing granted).</item>
/// <item>Warning (amber): not critical, and credits left between 10 % and 30 % inclusive, OR 7 to 30 days to expiry inclusive.</item>
/// <item>Healthy (green): more than 30 % credits left and more than 30 days left (or no expiry).</item>
/// </list>
/// Credits left are clamped to [0, total]. Days are fractional (<c>(expiry - now).TotalDays</c>).
/// </summary>
public static class LicenseHealth
{
    public const double CriticalPercent = 10;
    public const double WarningPercent = 30;
    public const double CriticalDays = 7;
    public const double WarningDays = 30;

    public static double PercentLeft(long remaining, long total) =>
        total <= 0 ? 0 : Math.Clamp(remaining, 0, total) * 100.0 / total;

    public static LicenseHealthLevel Evaluate(long remaining, long total, double? daysLeft)
    {
        var percent = PercentLeft(remaining, total);

        if (percent < CriticalPercent || daysLeft is < CriticalDays)
        {
            return LicenseHealthLevel.Critical;
        }

        if (percent <= WarningPercent || daysLeft is <= WarningDays)
        {
            return LicenseHealthLevel.Warning;
        }

        return LicenseHealthLevel.Healthy;
    }

    public static double? DaysLeft(DateTimeOffset? expiresAt, DateTimeOffset now) =>
        expiresAt is null ? null : (expiresAt.Value - now).TotalDays;
}
