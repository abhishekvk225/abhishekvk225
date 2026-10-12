using System.Globalization;
using System.Text.RegularExpressions;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Parsing;
using NexaVerify.Application.Verification.Processing;

namespace NexaVerify.Application.Verification.Pan;

/// <summary>Reads the printed fields of a PAN card from OCR / digital text lines. Pure and deterministic; label driven, no layout coordinates.</summary>
public static class PanDocumentReader
{
    private static readonly string[] DateFormats = ["dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy", "d/M/yyyy", "d-M-yyyy"];

    private static readonly Regex NameLabel = TextSupport.Compile(@"(?:^|[\s/|])Name\s*[:\-]?\s*(?<v>.*)$", ignoreCase: true);
    private static readonly Regex FatherLabel = TextSupport.Compile(@"Father'?s?\s*Name\s*[:\-]?\s*(?<v>.*)$", ignoreCase: true);
    private static readonly Regex DobLabel = TextSupport.Compile(@"(?:Date\s*of\s*Birth|D\.?O\.?B\.?)\s*[:\-]?\s*(?<v>.*)$", ignoreCase: true);
    private static readonly Regex DateToken = TextSupport.Compile(@"\b\d{1,2}[/\-.]\d{1,2}[/\-.]\d{4}\b");
    private static readonly Regex Letters = TextSupport.Compile(@"^[A-Za-z][A-Za-z .'\-]{1,59}$");
    private static readonly string[] Boilerplate = ["INCOME TAX", "GOVT", "GOVERNMENT", "PERMANENT ACCOUNT", "SIGNATURE", "DEPARTMENT"];

    public sealed record Result(PanData Data, PanValidation? Validation)
    {
        public override string ToString() => nameof(Result);
    }

    public static Result Read(DocumentText text, PanRuleOptions options)
    {
        var lines = TextSupport.Lines(text);
        var validation = FindNumber(lines, options);

        string? name = null, father = null;
        DateOnly? dob = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length > 80)
            {
                continue;
            }

            if (father is null && TextSupport.SafeMatch(FatherLabel, line) is { } fm)
            {
                father = ValueOrNext(fm.Groups["v"].Value, lines, i, IsNameLike);
                continue;
            }

            if (name is null && !line.Contains("father", StringComparison.OrdinalIgnoreCase) && TextSupport.SafeMatch(NameLabel, line) is { } nm)
            {
                name = ValueOrNext(nm.Groups["v"].Value, lines, i, IsNameLike);
                continue;
            }

            if (dob is null && TextSupport.SafeMatch(DobLabel, line) is { } dm)
            {
                dob = ParseDate(ValueOrNext(dm.Groups["v"].Value, lines, i, v => TextSupport.SafeMatch(DateToken, v) is not null));
            }
        }

        var data = new PanData
        {
            PanNumber = validation?.Normalized,
            HolderTypeCode = validation?.HolderTypeCode,
            NameOnCard = name is null ? null : TextSupport.Collapse(name).ToUpperInvariant(),
            FatherName = father is null ? null : TextSupport.Collapse(father).ToUpperInvariant(),
            DateOfBirth = dob,
            NumberOcrCorrected = validation?.OcrCorrected ?? false,
        };
        return new Result(data, validation);
    }

    private static PanValidation? FindNumber(List<string> lines, PanRuleOptions options)
    {
        PanValidation? repaired = null;
        foreach (var line in lines)
        {
            if (line.Length > 80)
            {
                continue;
            }

            var candidates = new List<string> { line };
            candidates.AddRange(line.Split([' ', '\t', ':', '|'], StringSplitOptions.RemoveEmptyEntries));
            foreach (var candidate in candidates)
            {
                var normalised = PanRules.Normalize(candidate);
                if (normalised.Length != 10)
                {
                    continue;
                }

                var exact = PanRules.Validate(normalised, options);
                if (exact.IsFormatValid)
                {
                    return exact;
                }

                repaired ??= PanRules.Validate(normalised, options, allowOcrRepair: true) is { IsFormatValid: true } r ? r : null;
            }
        }

        return repaired;
    }

    private static string? ValueOrNext(string inline, List<string> lines, int index, Func<string, bool> accept)
    {
        var value = inline.Trim();
        if (value.Length > 0)
        {
            return accept(value) ? value : null;
        }

        for (var j = index + 1; j < lines.Count && j <= index + 2; j++)
        {
            var candidate = lines[j];
            if (Boilerplate.Any(b => candidate.Contains(b, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return accept(candidate) ? candidate : null;
        }

        return null;
    }

    private static bool IsNameLike(string value) => TextSupport.SafeMatch(Letters, value) is not null;

    private static DateOnly? ParseDate(string? value)
    {
        if (value is null || TextSupport.SafeMatch(DateToken, value) is not { } m)
        {
            return null;
        }

        return DateOnly.TryParseExact(m.Value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}
