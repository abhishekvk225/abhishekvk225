using System.Globalization;
using System.Numerics;
using System.Text;

namespace NexaVerify.Web.Services;

/// <summary>
/// Money in minor units (cents, pence, yen...) with an ISO 4217 currency code. Formatting and parsing use integer/decimal arithmetic
/// only - never <c>double</c> - so no amount is ever off by a rounding error. Amounts shown to people always come from the API;
/// this helper only formats them (and turns an administrator's typed price into minor units).
/// </summary>
public static class Money
{
    /// <summary>Currencies with no minor unit (ISO 4217 exponent 0).</summary>
    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.Ordinal)
    {
        "BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW", "PYG", "RWF", "UGX", "UYI", "VND", "VUV", "XAF", "XOF", "XPF",
    };

    /// <summary>Currencies with three decimals (ISO 4217 exponent 3).</summary>
    private static readonly HashSet<string> ThreeDecimal = new(StringComparer.Ordinal)
    {
        "BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND",
    };

    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["USD"] = "$", ["EUR"] = "€", ["GBP"] = "£", ["INR"] = "₹", ["JPY"] = "¥", ["CNY"] = "CN¥", ["KRW"] = "₩",
        ["AUD"] = "A$", ["CAD"] = "CA$", ["NZD"] = "NZ$", ["SGD"] = "SGD ", ["HKD"] = "HK$", ["BRL"] = "R$", ["ILS"] = "₪",
        ["VND"] = "₫", ["PHP"] = "₱", ["MXN"] = "MX$", ["TWD"] = "NT$",
    };

    /// <summary>Currencies an administrator can choose for a credit pack (the API remains the authority on what it accepts).</summary>
    public static IReadOnlyList<string> SupportedCurrencies { get; } = ["USD", "EUR", "GBP", "INR", "AUD", "CAD", "SGD", "JPY", "KWD"];

    /// <summary>Number of decimals of the currency (0, 2 or 3). Unknown codes use 2.</summary>
    public static int Exponent(string? currency)
    {
        var code = Normalize(currency);
        return ZeroDecimal.Contains(code) ? 0 : ThreeDecimal.Contains(code) ? 3 : 2;
    }

    /// <summary>"$1,234.50", "¥1,500", "KWD 12.345". Negative amounts read "-$5.00". Unknown currency codes are shown as the code.</summary>
    public static string Format(long minor, string? currency)
    {
        var code = Normalize(currency);
        var exponent = Exponent(code);
        var negative = minor < 0;
        var magnitude = BigInteger.Abs(minor);
        var whole = BigInteger.DivRem(magnitude, BigInteger.Pow(10, exponent), out var fraction);

        var text = new StringBuilder();
        text.Append(WithGrouping(whole.ToString(CultureInfo.InvariantCulture)));
        if (exponent > 0)
        {
            text.Append('.').Append(fraction.ToString(CultureInfo.InvariantCulture).PadLeft(exponent, '0'));
        }

        var symbol = Symbols.TryGetValue(code, out var s) ? s : code.Length == 0 ? string.Empty : code + " ";
        return (negative ? "-" : string.Empty) + symbol + text;
    }

    /// <summary>The amount without a currency symbol, as typed into an input ("1234.50", "1500", "12.345").</summary>
    public static string ToInputText(long minor, string? currency)
    {
        var exponent = Exponent(currency);
        var negative = minor < 0;
        var whole = BigInteger.DivRem(BigInteger.Abs(minor), BigInteger.Pow(10, exponent), out var fraction);
        var text = whole.ToString(CultureInfo.InvariantCulture);
        if (exponent > 0)
        {
            text += "." + fraction.ToString(CultureInfo.InvariantCulture).PadLeft(exponent, '0');
        }

        return (negative ? "-" : string.Empty) + text;
    }

    /// <summary>
    /// Turns a typed major-unit amount ("12.5", "1,250.00", "1500") into minor units. Fails (null) for anything that is not an exact
    /// amount in this currency: text, negative numbers, more decimals than the currency has ("12.505" in USD, "1.5" in JPY), or a value
    /// that does not fit in 64 bits. Nothing is rounded.
    /// </summary>
    public static long? TryParseMajor(string? input, string? currency)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var text = input.Trim().Replace(",", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (text.Length == 0 || text.Length > 24)
        {
            return null;
        }

        var exponent = Exponent(currency);
        var parts = text.Split('.');
        if (parts.Length > 2)
        {
            return null;
        }

        var wholeText = parts[0];
        var fractionText = parts.Length == 2 ? parts[1] : string.Empty;
        if (wholeText.Length == 0 && fractionText.Length == 0)
        {
            return null;
        }

        if (wholeText.Length == 0)
        {
            wholeText = "0";
        }

        if (!wholeText.All(char.IsAsciiDigit) || !fractionText.All(char.IsAsciiDigit) || fractionText.Length > exponent)
        {
            return null;
        }

        var scaled = BigInteger.Parse(wholeText, CultureInfo.InvariantCulture) * BigInteger.Pow(10, exponent)
                     + (fractionText.Length == 0 ? BigInteger.Zero : BigInteger.Parse(fractionText.PadRight(exponent, '0'), CultureInfo.InvariantCulture));
        return scaled > long.MaxValue ? null : (long)scaled;
    }

    /// <summary>Tax part of a total that is already known: <c>total - price</c>. Integer arithmetic only.</summary>
    public static long Difference(long totalMinor, long priceMinor) => checked(totalMinor - priceMinor);

    /// <summary>True when pack A costs less per credit than pack B (exact cross-multiplication, no division, no floating point).</summary>
    public static bool CheaperPerCredit(long priceA, long creditsA, long priceB, long creditsB) =>
        creditsA > 0 && creditsB > 0 && new BigInteger(priceA) * creditsB < new BigInteger(priceB) * creditsA;

    private static string Normalize(string? currency) => (currency ?? string.Empty).Trim().ToUpperInvariant();

    private static string WithGrouping(string digits)
    {
        if (digits.Length <= 3)
        {
            return digits;
        }

        var builder = new StringBuilder(digits.Length + digits.Length / 3);
        var first = digits.Length % 3;
        if (first > 0)
        {
            builder.Append(digits, 0, first);
        }

        for (var i = first; i < digits.Length; i += 3)
        {
            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            builder.Append(digits, i, 3);
        }

        return builder.ToString();
    }
}
