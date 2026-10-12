using System.Globalization;
using System.Text;
using NexaVerify.Application.Verification.Abstractions;

namespace NexaVerify.Application.Verification.BankStatements;

/// <summary>Rule-set driven parsing of amounts and dates. Money is <c>decimal</c> rounded to two places at this boundary.</summary>
public static class AmountAndDateParsing
{
    private const int MaxAmountLength = 32;

    public enum Side
    {
        None = 0,
        Debit,
        Credit,
    }

    /// <summary>Parses "1,00,000.00", "1,500.00 Dr", "(250.00)", "Rs. 10.50". Fails for anything that is not a plain amount.</summary>
    public static bool TryParseAmount(string? raw, AmountFormat format, out decimal value, out Side side)
    {
        value = 0m;
        side = Side.None;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxAmountLength)
        {
            return false;
        }

        var s = raw.Trim();
        if (format.DrCrSuffix || s.EndsWith("Dr", StringComparison.OrdinalIgnoreCase) || s.EndsWith("Cr", StringComparison.OrdinalIgnoreCase))
        {
            if (s.EndsWith("Dr", StringComparison.OrdinalIgnoreCase))
            {
                side = Side.Debit;
                s = s[..^2];
            }
            else if (s.EndsWith("Cr", StringComparison.OrdinalIgnoreCase))
            {
                side = Side.Credit;
                s = s[..^2];
            }
        }

        s = s.Trim();
        var negative = false;
        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            negative = true;
            s = s[1..^1];
        }

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch) || ch == '₹')
            {
                continue;
            }

            sb.Append(ch);
        }

        var t = sb.ToString();
        foreach (var prefix in new[] { "INR", "RS." , "RS" })
        {
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                t = t[prefix.Length..];
                break;
            }
        }

        if (format.ThousandsSeparator.Length > 0)
        {
            t = t.Replace(format.ThousandsSeparator, string.Empty, StringComparison.Ordinal);
        }

        if (format.DecimalSeparator != ".")
        {
            t = t.Replace(format.DecimalSeparator, ".", StringComparison.Ordinal);
        }

        if (!decimal.TryParse(t, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        value = Math.Round(negative ? -parsed : parsed, 2, MidpointRounding.AwayFromZero);
        return true;
    }

    /// <summary>A balance with a Dr suffix is an overdrawn (negative) balance.</summary>
    public static bool TryParseBalance(string? raw, AmountFormat format, out decimal value)
    {
        if (!TryParseAmount(raw, format, out value, out var side))
        {
            return false;
        }

        if (side == Side.Debit)
        {
            value = -value;
        }

        return true;
    }

    public static bool TryParseDate(string? raw, IReadOnlyList<string> formats, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 20)
        {
            return false;
        }

        var text = raw.Trim();
        foreach (var format in formats)
        {
            if (DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                // Two-digit years: statements are 20xx documents.
                if (format.Contains("yy", StringComparison.Ordinal) && !format.Contains("yyy", StringComparison.Ordinal) && d.Year < 2000)
                {
                    d = d.AddYears(100);
                }

                date = d;
                return true;
            }
        }

        return false;
    }
}
