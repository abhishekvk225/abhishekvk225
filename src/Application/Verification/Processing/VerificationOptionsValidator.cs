using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace NexaVerify.Application.Verification.Processing;

/// <summary>Start-up validation of <see cref="VerificationOptions"/> (run through <c>ValidateOnStart</c>). Reports every problem at once; messages never contain configured values that could be secrets.</summary>
public sealed class VerificationOptionsValidator : IValidateOptions<VerificationOptions>
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly string[] OcrProviders = ["tesseract-cli", "none"];

    public ValidateOptionsResult Validate(string? name, VerificationOptions o)
    {
        var errors = new List<string>();

        var u = o.Upload;
        Positive(errors, u.MaxFileBytes, "Upload:MaxFileBytes");
        Positive(errors, u.RequestOverheadBytes, "Upload:RequestOverheadBytes");
        Positive(errors, u.MaxPages.BankStatement, "Upload:MaxPages:BankStatement");
        Positive(errors, u.MaxPages.Pan, "Upload:MaxPages:Pan");
        Positive(errors, u.MaxPages.Passport, "Upload:MaxPages:Passport");
        Positive(errors, u.MaxImagePixels, "Upload:MaxImagePixels");
        Positive(errors, u.InspectTimeoutSeconds, "Upload:InspectTimeoutSeconds");
        Positive(errors, u.InspectMaxConcurrent, "Upload:InspectMaxConcurrent");
        if (u.AllowedExtensions is not { Length: > 0 } || u.AllowedExtensions.Any(e => string.IsNullOrWhiteSpace(e) || !e.StartsWith('.')))
        {
            errors.Add("Upload:AllowedExtensions must list extensions that start with a dot.");
        }

        Positive(errors, o.Limits.MaxQueuedPerClient, "Limits:MaxQueuedPerClient");

        var w = o.Worker;
        Positive(errors, w.Parallelism, "Worker:Parallelism");
        Positive(errors, w.PollSeconds, "Worker:PollSeconds");
        Positive(errors, w.BatchSize, "Worker:BatchSize");
        var p = o.Processing;
        Positive(errors, p.TimeoutSeconds, "Processing:TimeoutSeconds");
        Positive(errors, p.MaxAttempts, "Processing:MaxAttempts");
        Positive(errors, p.MaxQueueAgeMinutes, "Processing:MaxQueueAgeMinutes");
        if (p.BackoffSeconds is not { Length: > 0 } || p.BackoffSeconds.Any(s => s < 1))
        {
            errors.Add("Processing:BackoffSeconds must list positive values.");
        }

        if (w.LeaseSeconds <= p.TimeoutSeconds + 30)
        {
            errors.Add("Worker:LeaseSeconds must exceed Processing:TimeoutSeconds + 30.");
        }

        var ocr = o.Ocr;
        if (!OcrProviders.Contains(ocr.Provider, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("Ocr:Provider must be 'tesseract-cli' or 'none'.");
        }

        if (ocr.Dpi is < 72 or > 600)
        {
            errors.Add("Ocr:Dpi must be between 72 and 600.");
        }

        if (ocr.MinTextCharsPerPage < 0)
        {
            errors.Add("Ocr:MinTextCharsPerPage must not be negative.");
        }

        Fraction(errors, ocr.MinMeanConfidence, "Ocr:MinMeanConfidence");
        if (string.Equals(ocr.Provider, "tesseract-cli", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(ocr.Tesseract.ExecutablePath) || string.IsNullOrWhiteSpace(ocr.Tesseract.Languages))
            {
                errors.Add("Ocr:Tesseract:ExecutablePath and :Languages are required.");
            }

            Positive(errors, ocr.Tesseract.TimeoutSecondsPerPage, "Ocr:Tesseract:TimeoutSecondsPerPage");
            Positive(errors, ocr.Tesseract.MaxConcurrent, "Ocr:Tesseract:MaxConcurrent");
        }

        var b = o.BankStatement;
        if (string.IsNullOrWhiteSpace(b.RuleSetsPath))
        {
            errors.Add("BankStatement:RuleSetsPath is required.");
        }

        if (b.BalanceTolerance < 0)
        {
            errors.Add("BankStatement:BalanceTolerance must not be negative.");
        }

        Positive(errors, b.MaxTransactions, "BankStatement:MaxTransactions");
        Positive(errors, b.MaxPeriodDays, "BankStatement:MaxPeriodDays");
        if (b.DefaultCurrency is not { Length: 3 })
        {
            errors.Add("BankStatement:DefaultCurrency must be a 3-letter ISO 4217 code.");
        }

        CheckPattern(errors, o.Pan.Pattern, "Pan:Pattern");
        if (o.Pan.HolderTypes is not { Length: > 0 } || o.Pan.HolderTypes.Any(h => h is not { Length: 1 } || !char.IsAsciiLetterUpper(h[0])))
        {
            errors.Add("Pan:HolderTypes must list single upper-case letters.");
        }

        CheckPattern(errors, o.Passport.DefaultNumberPattern, "Passport:DefaultNumberPattern");
        foreach (var (country, pattern) in o.Passport.NumberPatterns)
        {
            if (country is not { Length: 3 })
            {
                errors.Add("Passport:NumberPatterns keys must be ISO 3166 alpha-3 codes.");
            }

            CheckPattern(errors, pattern, "Passport:NumberPatterns");
        }

        Positive(errors, o.Passport.MaxValidityYears, "Passport:MaxValidityYears");

        Fraction(errors, o.Names.MatchThreshold, "Names:MatchThreshold");
        Fraction(errors, o.Names.PartialThreshold, "Names:PartialThreshold");
        if (o.Names.PartialThreshold > o.Names.MatchThreshold)
        {
            errors.Add("Names:PartialThreshold must not exceed Names:MatchThreshold.");
        }

        CheckProvider(errors, o.Providers.Pan, "Providers:Pan");
        CheckProvider(errors, o.Providers.Passport, "Providers:Passport");
        if (o.Providers.StoreRawResponse)
        {
            errors.Add("Providers:StoreRawResponse must be false: raw provider payloads contain personal data.");
        }

        var r = o.Retention;
        if (r.DocumentDays is < 1 or > RetentionOptions.MaxDocumentDays)
        {
            errors.Add($"Retention:DocumentDays must be between 1 and {RetentionOptions.MaxDocumentDays}.");
        }

        if (r.ResultDays is < 1 or > RetentionOptions.MaxResultDays)
        {
            errors.Add($"Retention:ResultDays must be between 1 and {RetentionOptions.MaxResultDays}.");
        }

        if (r.DocumentDays > r.ResultDays)
        {
            errors.Add("Retention:DocumentDays must not exceed Retention:ResultDays.");
        }

        Positive(errors, r.MetadataDays, "Retention:MetadataDays");
        Positive(errors, r.PersonalMetadataDays, "Retention:PersonalMetadataDays");

        Positive(errors, o.Result.InlineTransactions, "Result:InlineTransactions");
        Positive(errors, o.Download.LinkSeconds, "Download:LinkSeconds");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors.Select(e => $"{VerificationOptions.SectionName}:{e}"));
    }

    private static void Positive(List<string> errors, long value, string key)
    {
        if (value < 1)
        {
            errors.Add($"{key} must be greater than zero.");
        }
    }

    private static void Fraction(List<string> errors, decimal value, string key)
    {
        if (value is < 0 or > 1)
        {
            errors.Add($"{key} must be between 0 and 1.");
        }
    }

    private static void CheckProvider(List<string> errors, ProviderOptions provider, string key)
    {
        if (string.IsNullOrWhiteSpace(provider.Provider))
        {
            errors.Add($"{key}:Provider must be 'None' or a vendor id.");
        }

        Positive(errors, provider.TimeoutSeconds, $"{key}:TimeoutSeconds");
        Positive(errors, provider.MaxAttempts, $"{key}:MaxAttempts");
        Positive(errors, provider.BreakerFailures, $"{key}:BreakerFailures");
        Positive(errors, provider.BreakerOpenSeconds, $"{key}:BreakerOpenSeconds");
    }

    /// <summary>The pattern must compile; it runs with a match timeout wherever it is used (ReDoS).</summary>
    private static void CheckPattern(List<string> errors, string? pattern, string key)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            errors.Add($"{key} is required.");
            return;
        }

        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException)
        {
            errors.Add($"{key} is not a valid regular expression.");
        }
    }
}
