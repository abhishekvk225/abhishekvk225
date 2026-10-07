using System.Globalization;

namespace NexaVerify.Web.Components;

/// <summary>Small presentation helpers (formatting only, no rules).</summary>
public static class UiText
{
    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Relative(DateTimeOffset when, DateTimeOffset now)
    {
        var delta = now - when;
        if (delta < TimeSpan.Zero)
        {
            return "just now";
        }

        if (delta.TotalSeconds < 45)
        {
            return "just now";
        }

        if (delta.TotalMinutes < 60)
        {
            return Plural((int)Math.Max(1, Math.Round(delta.TotalMinutes)), "minute") + " ago";
        }

        if (delta.TotalHours < 24)
        {
            return Plural((int)Math.Round(delta.TotalHours), "hour") + " ago";
        }

        if (delta.TotalDays < 30)
        {
            return Plural((int)Math.Round(delta.TotalDays), "day") + " ago";
        }

        return when.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }

    public static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
