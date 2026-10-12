using System.Text;
using NexaVerify.Application.Verification.Parsing;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Pan;

/// <summary>Outcome of the format rules. <c>Normalized</c> is the number the rules ran on (repaired when <c>OcrCorrected</c>). A well-formed PAN says nothing about existence.</summary>
public sealed record PanValidation(
    string Normalized, bool IsFormatValid, bool OcrCorrected, string? HolderTypeCode, bool HolderTypeKnown, string? HolderTypeName)
{
    /// <summary>The number is personal data: never print it.</summary>
    public override string ToString() => nameof(PanValidation);
}

/// <summary>
/// PAN format rules (docs/07 §6.5). The pattern and the holder-type table are configuration. The final character is a check letter whose
/// algorithm is not public: it is NOT validated and nothing here may be read as "the PAN exists or is active".
/// </summary>
public static class PanRules
{
    private const int PanLength = 10;

    /// <summary>Holder types by 4th character (confirm against current Income Tax Department documentation before go-live).</summary>
    public static readonly IReadOnlyDictionary<string, string> HolderTypeNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["A"] = "Association of Persons",
        ["B"] = "Body of Individuals",
        ["C"] = "Company",
        ["F"] = "Firm / LLP",
        ["G"] = "Government",
        ["H"] = "Hindu Undivided Family",
        ["J"] = "Artificial Juridical Person",
        ["L"] = "Local Authority",
        ["P"] = "Individual (Person)",
        ["T"] = "Trust",
    };

    /// <summary>Trim, upper-case, drop spaces, hyphens and other separators.</summary>
    public static string Normalize(string? pan)
    {
        if (string.IsNullOrEmpty(pan))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(pan.Length);
        foreach (var ch in pan)
        {
            if (char.IsLetterOrDigit(ch) && ch < 128)
            {
                sb.Append(char.ToUpperInvariant(ch));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Validates the number. With <paramref name="allowOcrRepair"/> (document OCR, never a typed number) a confusable character in the wrong
    /// position class is repaired, and only when the repaired value satisfies the configured pattern.
    /// </summary>
    public static PanValidation Validate(string? pan, PanRuleOptions options, bool allowOcrRepair = false)
    {
        var normalized = Normalize(pan);
        var regex = TextSupport.Compile(options.Pattern);
        var corrected = false;

        if (!IsMatch(regex, normalized) && allowOcrRepair && normalized.Length == PanLength)
        {
            var repaired = Repair(normalized);
            if (!string.Equals(repaired, normalized, StringComparison.Ordinal) && IsMatch(regex, repaired))
            {
                normalized = repaired;
                corrected = true;
            }
        }

        var valid = IsMatch(regex, normalized);
        string? code = null;
        var known = false;
        string? name = null;
        if (valid && normalized.Length > 3)
        {
            code = normalized[3].ToString();
            known = options.HolderTypes.Contains(code, StringComparer.OrdinalIgnoreCase);
            if (known)
            {
                HolderTypeNames.TryGetValue(code, out name);
            }
        }

        return new PanValidation(normalized, valid, corrected, code, known, name);
    }

    /// <summary>First letters of the surname are expected in the 5th character for individuals ("P"); other holders use the entity name.</summary>
    public static bool FifthCharacterMatches(string normalizedPan, char? firstLetter) =>
        normalizedPan.Length >= 5 && firstLetter is { } c && char.ToUpperInvariant(c) == normalizedPan[4];

    /// <summary>Shows only the last characters (see <see cref="MaskedIdentifier"/>).</summary>
    public static string Mask(string? pan) => MaskedIdentifier.From(pan).Value;

    private static bool IsMatch(System.Text.RegularExpressions.Regex regex, string value)
    {
        try
        {
            return value.Length > 0 && regex.IsMatch(value);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return false;
        }
    }

    // Position classes of the standard layout: 5 letters, 4 digits, 1 letter. The configured pattern still decides acceptance.
    private static string Repair(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var digitPosition = i is >= 5 and <= 8;
            chars[i] = digitPosition ? ToDigit(chars[i]) : ToLetter(chars[i]);
        }

        return new string(chars);
    }

    private static char ToDigit(char c) => c switch
    {
        'O' or 'Q' or 'D' => '0',
        'I' or 'L' => '1',
        'Z' => '2',
        'S' => '5',
        'B' => '8',
        _ => c,
    };

    private static char ToLetter(char c) => c switch
    {
        '0' => 'O',
        '1' => 'I',
        '2' => 'Z',
        '5' => 'S',
        '8' => 'B',
        _ => c,
    };
}
