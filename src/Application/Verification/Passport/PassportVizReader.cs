using System.Globalization;
using System.Text.RegularExpressions;
using NexaVerify.Application.Verification.Parsing;

namespace NexaVerify.Application.Verification.Passport;

/// <summary>Values printed in the visual inspection zone. Personal data: the record never prints itself.</summary>
public sealed record PassportViz(
    string? PassportNumber, string? Surname, string? GivenNames, string? Nationality, DateOnly? DateOfBirth, DateOnly? DateOfIssue,
    DateOnly? DateOfExpiry, string? Sex)
{
    public bool IsEmpty => PassportNumber is null && Surname is null && GivenNames is null && Nationality is null
        && DateOfBirth is null && DateOfIssue is null && DateOfExpiry is null && Sex is null;

    public override string ToString() => nameof(PassportViz);
}

/// <summary>Label driven reader for the visual zone (<c>Passport No</c>, <c>Surname</c>, <c>Given Name(s)</c>, <c>Date of Issue</c> ...).</summary>
public static class PassportVizReader
{
    private const string Sep = @"[^A-Za-z0-9]*";

    private static readonly Regex Number = TextSupport.Compile(@"Passport\s*(?:No|Number)\b\.?" + Sep + "(?<v>.*)$", true);
    private static readonly Regex Surname = TextSupport.Compile(@"\bSurname\b" + Sep + "(?<v>.*)$", true);
    private static readonly Regex Given = TextSupport.Compile(@"Given\s*Name\(?s?\)?" + Sep + "(?<v>.*)$", true);
    private static readonly Regex Nationality = TextSupport.Compile(@"\bNationality\b" + Sep + "(?<v>.*)$", true);
    private static readonly Regex Dob = TextSupport.Compile(@"Date\s*of\s*Birth" + Sep + "(?<v>.*)$", true);
    private static readonly Regex Issue = TextSupport.Compile(@"Date\s*of\s*Issue" + Sep + "(?<v>.*)$", true);
    private static readonly Regex Expiry = TextSupport.Compile(@"Date\s*of\s*Expir(?:y|ation)" + Sep + "(?<v>.*)$", true);
    private static readonly Regex Sex = TextSupport.Compile(@"\bSex\b" + Sep + "(?<v>.*)$", true);
    private static readonly Regex NumericDate = TextSupport.Compile(@"\b(?<d>\d{1,2})[ /\-.](?<m>\d{1,2})[ /\-.](?<y>\d{4})\b");
    private static readonly Regex NamedDate = TextSupport.Compile(@"\b(?<d>\d{1,2})[ /\-.](?<m>[A-Za-z]{3})(?:/[A-Za-z]{3})?[ /\-.](?<y>\d{4})\b");
    private static readonly Regex NumberValue = TextSupport.Compile(@"\b(?<n>[A-Z][0-9]{7}|[A-Z0-9]{6,9})\b");
    private static readonly Regex NameValue = TextSupport.Compile(@"^[A-Za-z][A-Za-z .'\-]{0,58}$");

    public static PassportViz Read(IReadOnlyList<string> lines)
    {
        string? Value(Regex label, Func<string, bool> accept)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Length > 120 || TextSupport.SafeMatch(label, lines[i]) is not { } m)
                {
                    continue;
                }

                var inline = m.Groups["v"].Value.Trim();
                if (inline.Length > 0 && accept(inline))
                {
                    return inline;
                }

                if (inline.Length == 0 && i + 1 < lines.Count && accept(lines[i + 1]))
                {
                    return lines[i + 1].Trim();
                }
            }

            return null;
        }

        var number = Value(Number, v => TextSupport.SafeMatch(NumberValue, v.ToUpperInvariant()) is not null);
        var numberMatch = number is null ? null : TextSupport.SafeMatch(NumberValue, number.ToUpperInvariant());
        var nationality = Value(Nationality, v => v.Length is >= 3 and <= 40 && TextSupport.HasLetter(v));
        var sex = Value(Sex, v => v.Length > 0 && char.ToUpperInvariant(v[0]) is 'M' or 'F' or 'X');

        return new PassportViz(
            numberMatch?.Groups["n"].Value,
            Upper(Value(Surname, IsName)),
            Upper(Value(Given, IsName)),
            nationality is null ? null : TextSupport.Collapse(nationality).ToUpperInvariant(),
            Date(Value(Dob, IsDate)),
            Date(Value(Issue, IsDate)),
            Date(Value(Expiry, IsDate)),
            sex is null ? null : char.ToUpperInvariant(sex[0]).ToString());
    }

    private static string? Upper(string? v) => v is null ? null : TextSupport.Collapse(v).ToUpperInvariant();

    private static bool IsName(string v) => TextSupport.SafeMatch(NameValue, v.Trim()) is not null;

    private static bool IsDate(string v) => Date(v) is not null;

    private static DateOnly? Date(string? v)
    {
        if (v is null)
        {
            return null;
        }

        if (TextSupport.SafeMatch(NumericDate, v) is { } n
            && int.TryParse(n.Groups["d"].Value, out var d) && int.TryParse(n.Groups["m"].Value, out var mo) && int.TryParse(n.Groups["y"].Value, out var y)
            && Valid(y, mo, d))
        {
            return new DateOnly(y, mo, d);
        }

        if (TextSupport.SafeMatch(NamedDate, v) is { } m
            && int.TryParse(m.Groups["d"].Value, out d) && int.TryParse(m.Groups["y"].Value, out y)
            && DateTime.TryParseExact(m.Groups["m"].Value, "MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
            && Valid(y, month.Month, d))
        {
            return new DateOnly(y, month.Month, d);
        }

        return null;
    }

    private static bool Valid(int y, int m, int d) => y is >= 1900 and <= 2200 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m);
}
