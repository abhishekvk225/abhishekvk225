using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NexaVerify.Application.Tenancy;

/// <summary>Derives client codes (letters, digits and hyphens, at most 30) from a company name for self-service sign-up.</summary>
public static class ClientCodes
{
    public const int MaxBaseLength = 24;
    public const string Fallback = "CLIENT";

    private const string SuffixAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>"Zoë's Café & Co." becomes "ZOES-CAFE-CO": ASCII letters and digits, words joined by single hyphens, upper case.</summary>
    public static string BaseFrom(string? companyName)
    {
        var builder = new StringBuilder();
        var pendingHyphen = false;
        foreach (var rune in (companyName ?? string.Empty).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(rune))
            {
                if (pendingHyphen && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingHyphen = false;
                builder.Append(char.ToUpperInvariant(rune));
            }
            else if (rune is not ('\'' or '’'))
            {
                pendingHyphen = true;
            }
        }

        var code = builder.Length > MaxBaseLength ? builder.ToString(0, MaxBaseLength).TrimEnd('-') : builder.ToString();
        return code.Length == 0 ? Fallback : code;
    }

    /// <summary>The base with a random 4-character suffix, used when the plain code is taken.</summary>
    public static string WithRandomSuffix(string baseCode) =>
        baseCode + "-" + new string(Enumerable.Range(0, 4).Select(_ => SuffixAlphabet[RandomNumberGenerator.GetInt32(SuffixAlphabet.Length)]).ToArray());
}
