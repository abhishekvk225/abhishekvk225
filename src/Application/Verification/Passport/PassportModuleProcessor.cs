using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Parsing;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Application.Verification.Providers;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Passport;

/// <summary>
/// Passport pipeline: text -> TD3 MRZ and visual zone -> format, date, check-digit and cross-field checks -> comparison with the submitted
/// fields. MRZ validity proves internal consistency only; it is never described as the passport being genuine.
/// </summary>
public sealed class PassportModuleProcessor : IModuleProcessor
{
    private readonly IDocumentParser _parser;
    private readonly INameMatcher _names;
    private readonly IPassportVerificationProviderResolver _providers;
    private readonly VerificationOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<PassportModuleProcessor> _logger;

    public PassportModuleProcessor(
        IDocumentParser parser, INameMatcher names, IPassportVerificationProviderResolver providers, IOptions<VerificationOptions> options,
        TimeProvider clock, ILogger<PassportModuleProcessor> logger)
    {
        _parser = parser;
        _names = names;
        _providers = providers;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public VerificationType Type => VerificationType.Passport;

    public async Task<DocumentStageOutcome> ProcessDocumentAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        var parsed = await _parser.ParseAsync(context.Document, context.Kind, ProcessorSupport.ParseOptionsFor(_options, Type), cancellationToken).ConfigureAwait(false);
        if (parsed.IsFailure)
        {
            _logger.LogInformation("Passport text acquisition failed ({Code}) for {RequestId}", parsed.Error!.Code, context.Request.Id);
            return ProcessorSupport.Failed(ProcessorSupport.FailureCodeFor(parsed.Error));
        }

        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var lines = TextSupport.Lines(parsed.Value);
        var mrz = MrzTd3Parser.Parse(lines, today);
        var viz = PassportVizReader.Read(lines);
        var f = mrz.Fields;

        if (f is null && viz.IsEmpty)
        {
            return ProcessorSupport.Failed(VerificationRules.FailureCodes.DocumentUnreadable);
        }

        var data = new PassportData
        {
            PassportNumber = NullIfEmpty(f?.DocumentNumber) ?? viz.PassportNumber,
            IssuingCountry = NullIfEmpty(f?.IssuingState),
            Nationality = NullIfEmpty(f?.Nationality) ?? viz.Nationality,
            Surname = NullIfEmpty(f?.Surname) ?? viz.Surname,
            GivenNames = NullIfEmpty(f?.GivenNames) ?? viz.GivenNames,
            DateOfBirth = f?.DateOfBirth ?? viz.DateOfBirth,
            Sex = f?.Sex ?? viz.Sex,
            DateOfIssue = viz.DateOfIssue,
            DateOfExpiry = f?.DateOfExpiry ?? viz.DateOfExpiry,
            MrzPresent = f is not null,
            MrzFormat = f is not null ? "TD3" : null,
            MrzChecks = mrz.Checks,
        };

        var checks = new List<CheckResult>();
        checks.AddRange(MrzChecks(mrz));
        checks.AddRange(RuleChecks(data, f, today));
        checks.Add(ProcessorSupport.ConfidenceCheck("PP_EXTRACTION_CONFIDENCE", parsed.Value, _options));
        checks.AddRange(ConsistencyChecks(data, viz, context.SubmittedFields));

        _logger.LogInformation(
            "Passport processed for {RequestId}: MRZ {Mrz}, {Failed} failed checks", context.Request.Id, mrz.Status, checks.Count(c => c.Outcome == CheckOutcome.Fail));
        return new DocumentStageOutcome(StageState.Completed, StageState.Completed, StageState.Completed, data, checks, ProcessorSupport.ReasonsFrom(checks), null);
    }

    public async Task<ExternalStageOutcome> VerifyExternalAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        var provider = _providers.Resolve();
        var f = context.SubmittedFields;
        var data = context.Extracted as PassportData;
        var number = SubmittedFieldKeys.Get(f, SubmittedFieldKeys.PassportNumber) ?? data?.PassportNumber;
        var country = data?.IssuingCountry ?? SubmittedFieldKeys.Get(f, SubmittedFieldKeys.Nationality) ?? data?.Nationality;
        if (provider.IsConfigured && (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(country)))
        {
            return new ExternalStageOutcome(ExternalState.Inconclusive, provider.ProviderId, null, [], ProviderReasonCodes.InsufficientData);
        }

        var name = SubmittedFieldKeys.Get(f, SubmittedFieldKeys.FullName) ?? (data is null ? null : $"{data.GivenNames} {data.Surname}".Trim());
        var request = new PassportProviderRequest(
            number ?? string.Empty, country ?? string.Empty, name,
            SubmittedFieldKeys.GetDate(f, SubmittedFieldKeys.DateOfBirth) ?? data?.DateOfBirth,
            SubmittedFieldKeys.GetDate(f, SubmittedFieldKeys.DateOfExpiry) ?? data?.DateOfExpiry,
            context.CorrelationId);
        return await ProviderCallGuard.RunAsync(
            provider.ProviderId, provider.IsConfigured, _options.Providers.Passport.TimeoutSeconds,
            ct => provider.VerifyAsync(request, ct), _logger, cancellationToken).ConfigureAwait(false);
    }

    private static string? NullIfEmpty(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

    private static IEnumerable<CheckResult> MrzChecks(MrzParseResult mrz)
    {
        if (mrz.Status == MrzParseStatus.UnsupportedFormat)
        {
            yield return new CheckResult("MRZ_FORMAT_UNSUPPORTED", CheckStage.Rules, CheckOutcome.NotEvaluated, CheckSeverity.Info, null, "A machine-readable zone in a format other than TD3 was found; release 1 supports TD3 only.");
        }

        yield return mrz.Status == MrzParseStatus.Parsed
            ? new CheckResult("PP_MRZ_PRESENT", CheckStage.Rules, CheckOutcome.Pass, CheckSeverity.Info, null, "A TD3 machine-readable zone was found.")
            : new CheckResult("PP_MRZ_PRESENT", CheckStage.Rules, CheckOutcome.Warn, CheckSeverity.Review, null, "No readable TD3 machine-readable zone was found.");

        yield return Digit("PP_MRZ_CHECK_DOC", mrz.Checks.DocumentNumber, "document number");
        yield return Digit("PP_MRZ_CHECK_DOB", mrz.Checks.DateOfBirth, "date of birth");
        yield return Digit("PP_MRZ_CHECK_EXP", mrz.Checks.DateOfExpiry, "date of expiry");
        yield return Digit("PP_MRZ_CHECK_PERSONAL", mrz.Checks.PersonalNumber, "personal number");
        yield return Digit("PP_MRZ_CHECK_COMPOSITE", mrz.Checks.Composite, "composite");

        if (mrz.OcrCorrected)
        {
            yield return new CheckResult("MRZ_OCR_CORRECTED", CheckStage.Rules, CheckOutcome.Warn, CheckSeverity.Warning, null, "Characters of the machine-readable zone were corrected for text-recognition confusions; confirm them against the document.");
        }
    }

    private static CheckResult Digit(string code, MrzCheckState state, string what) => state switch
    {
        MrzCheckState.Valid => new CheckResult(code, CheckStage.Rules, CheckOutcome.Pass, CheckSeverity.Info, null, $"The {what} check digit is consistent. This does not show that the passport is genuine."),
        MrzCheckState.Invalid => new CheckResult(code, CheckStage.Rules, CheckOutcome.Fail, CheckSeverity.Review, null, $"The {what} check digit is inconsistent."),
        _ => new CheckResult(code, CheckStage.Rules, CheckOutcome.NotEvaluated, CheckSeverity.Info, null, $"The {what} check digit could not be evaluated."),
    };

    private IEnumerable<CheckResult> RuleChecks(PassportData d, MrzTd3Fields? f, DateOnly today)
    {
        // Number format by issuing country.
        if (string.IsNullOrWhiteSpace(d.PassportNumber))
        {
            yield return new CheckResult("PP_NUMBER_FORMAT", CheckStage.Rules, CheckOutcome.Fail, CheckSeverity.Review, "passportNumber", "No passport number could be read.");
        }
        else
        {
            var key = d.IssuingCountry ?? d.Nationality ?? string.Empty;
            var pattern = _options.Passport.NumberPatterns.TryGetValue(key, out var p) ? p : _options.Passport.DefaultNumberPattern;
            var ok = TextSupport.SafeMatch(TextSupport.Compile(pattern), d.PassportNumber.ToUpperInvariant()) is not null;
            yield return ok
                ? new CheckResult("PP_NUMBER_FORMAT", CheckStage.Rules, CheckOutcome.Pass, CheckSeverity.Info, "passportNumber", "The passport number has the expected format.")
                : new CheckResult("PP_NUMBER_FORMAT", CheckStage.Rules, CheckOutcome.Fail, CheckSeverity.Review, "passportNumber", "The passport number does not have the expected format.");
        }

        var datesInvalid = (f is not null && (f.DateOfBirth is null || f.DateOfExpiry is null)) || (f is null && (d.DateOfBirth is null || d.DateOfExpiry is null));
        yield return datesInvalid
            ? new CheckResult("PP_DATES_VALID", CheckStage.Rules, f is null ? CheckOutcome.Warn : CheckOutcome.Fail, CheckSeverity.Review, null, "A date of birth or expiry is missing or is not a valid calendar date.")
            : new CheckResult("PP_DATES_VALID", CheckStage.Rules, CheckOutcome.Pass, CheckSeverity.Info, null, "Dates are valid calendar dates.");

        yield return d.DateOfExpiry is not { } exp
            ? new CheckResult("PP_NOT_EXPIRED", CheckStage.Rules, CheckOutcome.NotEvaluated, CheckSeverity.Info, "dateOfExpiry", "No expiry date is available.")
            : exp >= today
                ? new CheckResult("PP_NOT_EXPIRED", CheckStage.Rules, CheckOutcome.Pass, CheckSeverity.Info, "dateOfExpiry", "The passport has not expired.")
                : new CheckResult("PP_NOT_EXPIRED", CheckStage.Rules, CheckOutcome.Fail, CheckSeverity.Review, "dateOfExpiry", "The passport has expired.");

        if (d.DateOfIssue is { } issue && d.DateOfExpiry is { } expiry)
        {
            yield return issue < expiry
                ? new CheckResult("PP_ISSUE_BEFORE_EXPIRY", CheckStage.Rules, CheckOutcome.Pass, CheckSeverity.Info, null, "The issue date is before the expiry date.")
                : new CheckResult("PP_ISSUE_BEFORE_EXPIRY", CheckStage.Rules, CheckOutcome.Fail, CheckSeverity.Review, null, "The issue date is not before the expiry date.");
            yield return expiry > issue.AddYears(_options.Passport.MaxValidityYears)
                ? new CheckResult("PP_VALIDITY_SPAN", CheckStage.Rules, CheckOutcome.Warn, CheckSeverity.Review, null, "The validity period is longer than the configured maximum.")
                : new CheckResult("PP_VALIDITY_SPAN", CheckStage.Rules, CheckOutcome.Pass, CheckSeverity.Info, null, "The validity period is within the configured maximum.");
        }
        else
        {
            yield return new CheckResult("PP_ISSUE_BEFORE_EXPIRY", CheckStage.Rules, CheckOutcome.NotEvaluated, CheckSeverity.Info, null, "The issue date is not available (it is printed in the visual zone only).");
            yield return new CheckResult("PP_VALIDITY_SPAN", CheckStage.Rules, CheckOutcome.NotEvaluated, CheckSeverity.Info, null, "The issue or expiry date is not available.");
        }

        yield return d.DateOfBirth is not { } dob
            ? new CheckResult("PP_AGE_PLAUSIBLE", CheckStage.Rules, CheckOutcome.NotEvaluated, CheckSeverity.Info, "dateOfBirth", "No date of birth is available.")
            : dob > today || dob.AddYears(_options.Passport.MaxAgeYears) < today
                ? new CheckResult("PP_AGE_PLAUSIBLE", CheckStage.Rules, CheckOutcome.Fail, CheckSeverity.Review, "dateOfBirth", "The date of birth is in the future or implies an implausible age.")
                : new CheckResult("PP_AGE_PLAUSIBLE", CheckStage.Rules, CheckOutcome.Pass, CheckSeverity.Info, "dateOfBirth", "The date of birth implies a plausible age.");
    }

    private IEnumerable<CheckResult> ConsistencyChecks(PassportData d, PassportViz viz, IReadOnlyDictionary<string, string?> f)
    {
        yield return VizConsistency(d, viz);

        var extractedName = string.IsNullOrWhiteSpace(d.Surname) && string.IsNullOrWhiteSpace(d.GivenNames) ? null : $"{d.GivenNames} {d.Surname}".Trim();
        var submittedNumber = SubmittedFieldKeys.Get(f, SubmittedFieldKeys.PassportNumber);
        var numberOutcome = submittedNumber is null ? FieldMatchOutcome.NotProvided
            : d.PassportNumber is null ? FieldMatchOutcome.NotExtracted
            : string.Equals(submittedNumber.Replace(" ", string.Empty, StringComparison.Ordinal), d.PassportNumber, StringComparison.OrdinalIgnoreCase) ? FieldMatchOutcome.Match : FieldMatchOutcome.Mismatch;
        var submittedNationality = SubmittedFieldKeys.Get(f, SubmittedFieldKeys.Nationality);
        var nationalityOutcome = submittedNationality is null ? FieldMatchOutcome.NotProvided
            : d.Nationality is null ? FieldMatchOutcome.NotExtracted
            : string.Equals(submittedNationality, d.Nationality, StringComparison.OrdinalIgnoreCase) ? FieldMatchOutcome.Match : FieldMatchOutcome.Mismatch;

        yield return ProcessorSupport.FieldCheck("PP_NUMBER_MATCH", "passportNumber", numberOutcome);
        yield return ProcessorSupport.FieldCheck("PP_NAME_MATCH", "fullName", _names.Compare(SubmittedFieldKeys.Get(f, SubmittedFieldKeys.FullName), extractedName).Outcome);
        yield return ProcessorSupport.FieldCheck("PP_DOB_MATCH", "dateOfBirth", DateOutcome(SubmittedFieldKeys.GetDate(f, SubmittedFieldKeys.DateOfBirth), d.DateOfBirth));
        yield return ProcessorSupport.FieldCheck("PP_EXPIRY_MATCH", "dateOfExpiry", DateOutcome(SubmittedFieldKeys.GetDate(f, SubmittedFieldKeys.DateOfExpiry), d.DateOfExpiry));
        yield return ProcessorSupport.FieldCheck("PP_ISSUE_MATCH", "dateOfIssue", DateOutcome(SubmittedFieldKeys.GetDate(f, SubmittedFieldKeys.DateOfIssue), d.DateOfIssue));
        yield return ProcessorSupport.FieldCheck("PP_NATIONALITY_MATCH", "nationality", nationalityOutcome);
    }

    private CheckResult VizConsistency(PassportData d, PassportViz viz)
    {
        if (!d.MrzPresent || viz.IsEmpty)
        {
            return new CheckResult("PP_VIZ_MRZ_CONSISTENT", CheckStage.Consistency, CheckOutcome.NotEvaluated, CheckSeverity.Info, null, "Both the machine-readable zone and the visual zone are needed to compare them.");
        }

        var diffs = 0;
        var compared = 0;
        void Cmp(string? mrz, string? vizValue)
        {
            if (mrz is null || vizValue is null)
            {
                return;
            }

            compared++;
            if (!string.Equals(mrz, vizValue, StringComparison.OrdinalIgnoreCase))
            {
                diffs++;
            }
        }

        Cmp(d.PassportNumber, viz.PassportNumber);
        if (d.DateOfBirth is { } a && viz.DateOfBirth is { } b) { compared++; diffs += a == b ? 0 : 1; }
        if (d.DateOfExpiry is { } c && viz.DateOfExpiry is { } e) { compared++; diffs += c == e ? 0 : 1; }
        if (d.Surname is not null && viz.Surname is not null)
        {
            compared++;
            diffs += _names.Compare(d.Surname, viz.Surname).Outcome is FieldMatchOutcome.Match ? 0 : 1;
        }

        if (d.GivenNames is not null && viz.GivenNames is not null)
        {
            compared++;
            diffs += _names.Compare(d.GivenNames, viz.GivenNames).Outcome is FieldMatchOutcome.Match or FieldMatchOutcome.PartialMatch ? 0 : 1;
        }

        if (compared == 0)
        {
            return new CheckResult("PP_VIZ_MRZ_CONSISTENT", CheckStage.Consistency, CheckOutcome.NotEvaluated, CheckSeverity.Info, null, "No field was readable in both zones.");
        }

        return diffs == 0
            ? new CheckResult("PP_VIZ_MRZ_CONSISTENT", CheckStage.Consistency, CheckOutcome.Pass, CheckSeverity.Info, null, $"{compared} field(s) agree between the two zones.")
            : new CheckResult("PP_VIZ_MRZ_CONSISTENT", CheckStage.Consistency, CheckOutcome.Fail, CheckSeverity.Review, null, $"{diffs} of {compared} field(s) differ between the visual zone and the machine-readable zone.");
    }

    private static FieldMatchOutcome DateOutcome(DateOnly? submitted, DateOnly? extracted) =>
        submitted is null ? FieldMatchOutcome.NotProvided
        : extracted is null ? FieldMatchOutcome.NotExtracted
        : submitted == extracted ? FieldMatchOutcome.Match : FieldMatchOutcome.Mismatch;
}
