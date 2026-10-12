using System.Globalization;
using System.Text;
using NexaVerify.Application.Verification.Abstractions;

namespace NexaVerify.Application.Verification.Passport;

public enum MrzParseStatus
{
    /// <summary>No machine-readable zone was found in the candidate lines.</summary>
    NotFound = 0,

    /// <summary>A TD3 zone (2 x 44) was parsed. The check states say whether it is internally consistent.</summary>
    Parsed,

    /// <summary>A zone in another format (TD1 3x30, TD2 2x36) was seen; release 1 supports TD3 only.</summary>
    UnsupportedFormat,
}

/// <summary>The fields of a TD3 zone. Personal data: the record never prints itself.</summary>
public sealed record MrzTd3Fields(
    string DocumentType, string IssuingState, string Surname, string GivenNames, string DocumentNumber, string Nationality,
    DateOnly? DateOfBirth, string DateOfBirthRaw, string Sex, DateOnly? DateOfExpiry, string DateOfExpiryRaw, string PersonalNumber)
{
    public override string ToString() => nameof(MrzTd3Fields);
}

public sealed record MrzParseResult(MrzParseStatus Status, MrzTd3Fields? Fields, MrzCheckStates Checks, bool OcrCorrected)
{
    public static readonly MrzCheckStates NoChecks = new(
        MrzCheckState.NotPresent, MrzCheckState.NotPresent, MrzCheckState.NotPresent, MrzCheckState.NotPresent, MrzCheckState.NotPresent);

    public override string ToString() => $"{nameof(MrzParseResult)}({Status})";
}

/// <summary>
/// TD3 machine-readable zone parser (docs/07 §6.6). Validity of the check digits proves internal consistency only and is never evidence
/// that the passport is genuine. OCR confusions are repaired per field class, never silently: <see cref="MrzParseResult.OcrCorrected"/>.
/// </summary>
public static class MrzTd3Parser
{
    private const int Td3Length = 44;

    public static MrzParseResult Parse(IReadOnlyList<string> candidateLines, DateOnly today)
    {
        var lines = candidateLines.Select(Sanitize).Where(l => l.Length >= 20).ToList();

        // Prefer the LAST two consecutive lines that look like TD3 (the zone sits at the bottom of the data page).
        for (var i = lines.Count - 2; i >= 0; i--)
        {
            var first = lines[i];
            var second = lines[i + 1];
            if (first.Length == Td3Length && second.Length == Td3Length && first[0] == 'P' && IsMrzCharset(first, second))
            {
                return Build(first, second, today);
            }
        }

        // Tolerate OCR noise: a pair that becomes 44 characters of the MRZ charset after zone repair.
        for (var i = lines.Count - 2; i >= 0; i--)
        {
            var first = lines[i];
            var second = lines[i + 1];
            if (first.Length == Td3Length && second.Length == Td3Length && first[0] == 'P')
            {
                return Build(first, second, today);
            }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var td1 = i + 2 < lines.Count && l.Length == 30 && lines[i + 1].Length == 30 && lines[i + 2].Length == 30;
            var td2 = i + 1 < lines.Count && l.Length == 36 && lines[i + 1].Length == 36 && IsMrzCharset(l, lines[i + 1]);
            if (td1 || td2)
            {
                return new MrzParseResult(MrzParseStatus.UnsupportedFormat, null, MrzParseResult.NoChecks, false);
            }
        }

        return new MrzParseResult(MrzParseStatus.NotFound, null, MrzParseResult.NoChecks, false);
    }

    /// <summary>Removes whitespace and maps the look-alike chevrons to <c>&lt;</c>; upper-cases.</summary>
    public static string Sanitize(string line)
    {
        var sb = new StringBuilder(line.Length);
        foreach (var ch in line)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            sb.Append(ch switch
            {
                '«' or '‹' or '〈' or '＜' => '<',
                _ => char.ToUpperInvariant(ch),
            });
        }

        return sb.ToString();
    }

    private static bool IsMrzCharset(params string[] lines) =>
        lines.All(l => l.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or '<'));

    private static MrzParseResult Build(string line1, string line2, DateOnly today)
    {
        var raw = Evaluate(line1, line2, today);
        var repaired1 = RepairLine1(line1);
        var repaired2 = RepairLine2(line2);
        if (string.Equals(repaired1, line1, StringComparison.Ordinal) && string.Equals(repaired2, line2, StringComparison.Ordinal))
        {
            return raw;
        }

        var fixedUp = Evaluate(repaired1, repaired2, today);
        return Passing(fixedUp.Checks) > Passing(raw.Checks) || (Passing(fixedUp.Checks) == Passing(raw.Checks) && HasNonMrz(line1, line2))
            ? fixedUp with { OcrCorrected = true }
            : raw;
    }

    private static bool HasNonMrz(string a, string b) => !IsMrzCharset(a, b);

    private static int Passing(MrzCheckStates c) =>
        new[] { c.DocumentNumber, c.DateOfBirth, c.DateOfExpiry, c.PersonalNumber, c.Composite }.Count(s => s == MrzCheckState.Valid);

    private static MrzParseResult Evaluate(string l1, string l2, DateOnly today)
    {
        var docNumber = l2[..9];
        var dob = l2.Substring(13, 6);
        var exp = l2.Substring(21, 6);
        var personal = l2.Substring(28, 14);

        var checks = new MrzCheckStates(
            State(MrzCheckDigit.IsValid(docNumber, l2[9])),
            State(MrzCheckDigit.IsValid(dob, l2[19])),
            State(MrzCheckDigit.IsValid(exp, l2[27])),
            PersonalState(personal, l2[42]),
            State(MrzCheckDigit.IsValid(l2[..10] + l2.Substring(13, 7) + l2.Substring(21, 22), l2[43])));

        var nameZone = l1[5..];
        var parts = nameZone.Split("<<", 2, StringSplitOptions.None);
        var surname = Clean(parts[0]);
        var given = parts.Length > 1 ? Clean(parts[1]) : string.Empty;

        var fields = new MrzTd3Fields(
            DocumentType: l1[..2].Replace('<', ' ').Trim(),
            IssuingState: l1.Substring(2, 3).Replace("<", string.Empty, StringComparison.Ordinal),
            Surname: surname,
            GivenNames: given,
            DocumentNumber: docNumber.TrimEnd('<'),
            Nationality: l2.Substring(10, 3).Replace("<", string.Empty, StringComparison.Ordinal),
            DateOfBirth: ParseBirth(dob, today),
            DateOfBirthRaw: dob,
            Sex: l2[20] switch { 'M' => "M", 'F' => "F", _ => "X" },
            DateOfExpiry: ParseExpiry(exp),
            DateOfExpiryRaw: exp,
            PersonalNumber: personal.TrimEnd('<'));

        return new MrzParseResult(MrzParseStatus.Parsed, fields, checks, false);
    }

    private static MrzCheckState State(bool valid) => valid ? MrzCheckState.Valid : MrzCheckState.Invalid;

    private static MrzCheckState PersonalState(string personal, char check)
    {
        if (personal.All(c => c == '<'))
        {
            return check is '<' or '0' ? MrzCheckState.NotPresent : MrzCheckState.Invalid;
        }

        return State(MrzCheckDigit.IsValid(personal, check));
    }

    private static string Clean(string value) => TextCollapse(value.Replace('<', ' '));

    private static string TextCollapse(string value) => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Date of birth: 20YY unless that lies in the future, then 19YY.</summary>
    private static DateOnly? ParseBirth(string yymmdd, DateOnly today)
    {
        if (!TryParts(yymmdd, out var yy, out var mm, out var dd))
        {
            return null;
        }

        var year = 2000 + yy;
        if (!TryDate(year, mm, dd, out var date) || date > today)
        {
            year = 1900 + yy;
            return TryDate(year, mm, dd, out date) ? date : null;
        }

        return date;
    }

    private static DateOnly? ParseExpiry(string yymmdd) =>
        TryParts(yymmdd, out var yy, out var mm, out var dd) && TryDate(2000 + yy, mm, dd, out var date) ? date : null;

    private static bool TryParts(string s, out int yy, out int mm, out int dd)
    {
        yy = mm = dd = 0;
        return s.Length == 6
            && int.TryParse(s.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out yy)
            && int.TryParse(s.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out mm)
            && int.TryParse(s.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out dd);
    }

    private static bool TryDate(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }

    // ---- OCR confusion repair per field class (never applied silently: the caller reports OcrCorrected) -------------------------

    private static string RepairLine1(string l1)
    {
        var c = l1.ToCharArray();
        for (var i = 2; i < c.Length; i++)
        {
            c[i] = ToAlpha(c[i]); // issuing state and name zone are alphabetic
        }

        return new string(c);
    }

    private static string RepairLine2(string l2)
    {
        var c = l2.ToCharArray();
        for (var i = 0; i < c.Length; i++)
        {
            var numeric = i is 9 or (>= 13 and <= 19) or (>= 21 and <= 27) or 42 or 43;
            var alpha = i is >= 10 and <= 12;
            if (numeric)
            {
                c[i] = ToDigit(c[i]);
            }
            else if (alpha)
            {
                c[i] = ToAlpha(c[i]);
            }
        }

        return new string(c);
    }

    private static char ToDigit(char c) => c switch
    {
        'O' or 'Q' or 'D' => '0',
        'I' or 'L' => '1',
        'Z' => '2',
        'S' => '5',
        'G' => '6',
        'B' => '8',
        _ => c,
    };

    private static char ToAlpha(char c) => c switch
    {
        '0' => 'O',
        '1' => 'I',
        '2' => 'Z',
        '5' => 'S',
        '6' => 'G',
        '8' => 'B',
        _ => c,
    };
}
