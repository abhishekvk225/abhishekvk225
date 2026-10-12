using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Parsing;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Application.Verification.Providers;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Pan;

/// <summary>
/// PAN pipeline. A request may be number-only (no document): extraction is then Skipped and the format rules run on the submitted number.
/// A well-formed number is never described as existing, active or belonging to anyone; only an authoritative provider answer can do that.
/// </summary>
public sealed class PanModuleProcessor : IModuleProcessor
{
    private readonly IDocumentParser _parser;
    private readonly INameMatcher _names;
    private readonly IPanVerificationProviderResolver _providers;
    private readonly VerificationOptions _options;
    private readonly ILogger<PanModuleProcessor> _logger;

    public PanModuleProcessor(
        IDocumentParser parser, INameMatcher names, IPanVerificationProviderResolver providers, IOptions<VerificationOptions> options,
        ILogger<PanModuleProcessor> logger)
    {
        _parser = parser;
        _names = names;
        _providers = providers;
        _options = options.Value;
        _logger = logger;
    }

    public VerificationType Type => VerificationType.Pan;

    public async Task<DocumentStageOutcome> ProcessDocumentAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        var f = context.SubmittedFields;
        var submittedPan = SubmittedFieldKeys.Get(f, SubmittedFieldKeys.PanNumber);

        if (context.Document.IsEmpty)
        {
            return submittedPan is null
                ? ProcessorSupport.Failed(VerificationRules.FailureCodes.DocumentUnreadable)
                : NumberOnly(submittedPan, f);
        }

        var parsed = await _parser.ParseAsync(context.Document, context.Kind, ProcessorSupport.ParseOptionsFor(_options, Type), cancellationToken).ConfigureAwait(false);
        if (parsed.IsFailure)
        {
            _logger.LogInformation("PAN text acquisition failed ({Code}) for {RequestId}", parsed.Error!.Code, context.Request.Id);
            return ProcessorSupport.Failed(ProcessorSupport.FailureCodeFor(parsed.Error));
        }

        var read = PanDocumentReader.Read(parsed.Value, _options.Pan);
        var data = read.Data;
        if (data.PanNumber is null && data.NameOnCard is null && data.DateOfBirth is null)
        {
            return ProcessorSupport.Failed(VerificationRules.FailureCodes.DocumentUnreadable);
        }

        var checks = new List<CheckResult>();
        checks.AddRange(FormatChecks(read.Validation, hasNumber: data.PanNumber is not null));
        checks.Add(ProcessorSupport.ConfidenceCheck("PAN_EXTRACTION_CONFIDENCE", parsed.Value, _options));

        var submittedName = SubmittedFieldKeys.Get(f, SubmittedFieldKeys.FullName);
        checks.Add(ProcessorSupport.FieldCheck("PAN_DOC_NUMBER_MATCH", "panNumber", NumberMatch(submittedPan, data.PanNumber)));
        checks.Add(ProcessorSupport.FieldCheck("PAN_NAME_MATCH", "fullName", _names.Compare(submittedName, data.NameOnCard).Outcome));
        checks.Add(ProcessorSupport.FieldCheck("PAN_DOB_MATCH", "dateOfBirth", DateMatch(SubmittedFieldKeys.GetDate(f, SubmittedFieldKeys.DateOfBirth), data.DateOfBirth)));
        checks.Add(ProcessorSupport.FieldCheck("PAN_FATHER_NAME_MATCH", "fatherName", _names.Compare(SubmittedFieldKeys.Get(f, SubmittedFieldKeys.FatherName), data.FatherName).Outcome));
        checks.Add(InitialCheck(data.PanNumber, submittedName ?? data.NameOnCard));

        _logger.LogInformation("PAN document processed for {RequestId}: {Failed} failed checks", context.Request.Id, checks.Count(c => c.Outcome == CheckOutcome.Fail));
        return new DocumentStageOutcome(StageState.Completed, StageState.Completed, StageState.Completed, data, checks, ProcessorSupport.ReasonsFrom(checks), null);
    }

    public async Task<ExternalStageOutcome> VerifyExternalAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        var provider = _providers.Resolve();
        var pan = SubmittedFieldKeys.Get(context.SubmittedFields, SubmittedFieldKeys.PanNumber) ?? (context.Extracted as PanData)?.PanNumber;
        var validation = PanRules.Validate(pan, _options.Pan);
        if (provider.IsConfigured && !validation.IsFormatValid)
        {
            return new ExternalStageOutcome(ExternalState.Inconclusive, provider.ProviderId, null, [], ProviderReasonCodes.InsufficientData);
        }

        var name = SubmittedFieldKeys.Get(context.SubmittedFields, SubmittedFieldKeys.FullName) ?? (context.Extracted as PanData)?.NameOnCard;
        var dob = SubmittedFieldKeys.GetDate(context.SubmittedFields, SubmittedFieldKeys.DateOfBirth) ?? (context.Extracted as PanData)?.DateOfBirth;
        var request = new PanProviderRequest(validation.Normalized, name, dob, context.CorrelationId);
        return await ProviderCallGuard.RunAsync(
            provider.ProviderId, provider.IsConfigured, _options.Providers.Pan.TimeoutSeconds,
            ct => provider.VerifyAsync(request, ct), _logger, cancellationToken).ConfigureAwait(false);
    }

    private DocumentStageOutcome NumberOnly(string submittedPan, IReadOnlyDictionary<string, string?> f)
    {
        var v = PanRules.Validate(submittedPan, _options.Pan);
        var data = new PanData { PanNumber = v.IsFormatValid ? v.Normalized : null, HolderTypeCode = v.HolderTypeCode };
        var checks = new List<CheckResult>(FormatChecks(v, hasNumber: true))
        {
            InitialCheck(v.IsFormatValid ? v.Normalized : null, SubmittedFieldKeys.Get(f, SubmittedFieldKeys.FullName)),
        };
        return new DocumentStageOutcome(StageState.Skipped, StageState.Completed, StageState.Skipped, data, checks, ProcessorSupport.ReasonsFrom(checks), null);
    }

    private static IEnumerable<CheckResult> FormatChecks(PanValidation? v, bool hasNumber)
    {
        if (!hasNumber || v is null)
        {
            yield return new CheckResult("PAN_FORMAT", CheckStage.Rules, CheckOutcome.Fail, CheckSeverity.Review, "panNumber", "No PAN number could be read from the document.");
            yield return new CheckResult("PAN_HOLDER_TYPE", CheckStage.Rules, CheckOutcome.NotEvaluated, CheckSeverity.Info, "panNumber", "No valid number to read the holder type from.");
            yield break;
        }

        yield return v.IsFormatValid
            ? new CheckResult("PAN_FORMAT", CheckStage.Rules, CheckOutcome.Pass, CheckSeverity.Info, "panNumber", "The number has the expected format. This does not show that the PAN exists or is active.")
            : new CheckResult("PAN_FORMAT", CheckStage.Rules, CheckOutcome.Fail, CheckSeverity.Review, "panNumber", "The number does not have the expected format.");

        if (!v.IsFormatValid)
        {
            yield return new CheckResult("PAN_HOLDER_TYPE", CheckStage.Rules, CheckOutcome.NotEvaluated, CheckSeverity.Info, "panNumber", "No valid number to read the holder type from.");
            yield break;
        }

        yield return v.HolderTypeKnown
            ? new CheckResult("PAN_HOLDER_TYPE", CheckStage.Rules, CheckOutcome.Pass, CheckSeverity.Info, "panNumber", "The holder type character is in the configured table.")
            : new CheckResult("PAN_HOLDER_TYPE", CheckStage.Rules, CheckOutcome.Fail, CheckSeverity.Review, "panNumber", "PAN_HOLDER_TYPE_UNKNOWN: the holder type character is not in the configured table.");

        if (v.OcrCorrected)
        {
            yield return new CheckResult("PAN_OCR_CORRECTED", CheckStage.Rules, CheckOutcome.Warn, CheckSeverity.Warning, "panNumber", "Characters of the number were corrected for text-recognition confusions; confirm it against the document.");
        }
    }

    private static FieldMatchOutcome NumberMatch(string? submitted, string? extracted)
    {
        if (submitted is null)
        {
            return FieldMatchOutcome.NotProvided;
        }

        if (extracted is null)
        {
            return FieldMatchOutcome.NotExtracted;
        }

        return string.Equals(PanRules.Normalize(submitted), PanRules.Normalize(extracted), StringComparison.Ordinal) ? FieldMatchOutcome.Match : FieldMatchOutcome.Mismatch;
    }

    private static FieldMatchOutcome DateMatch(DateOnly? submitted, DateOnly? extracted) =>
        submitted is null ? FieldMatchOutcome.NotProvided
        : extracted is null ? FieldMatchOutcome.NotExtracted
        : submitted == extracted ? FieldMatchOutcome.Match : FieldMatchOutcome.Mismatch;

    /// <summary>5th character = first letter of the surname (individuals) or entity name: a Warning only, because name order varies.</summary>
    private static CheckResult InitialCheck(string? normalizedPan, string? name)
    {
        if (normalizedPan is not { Length: >= 5 } || string.IsNullOrWhiteSpace(name))
        {
            return new CheckResult("PAN_NAME_INITIAL", CheckStage.Consistency, CheckOutcome.NotEvaluated, CheckSeverity.Info, "fullName", "The number or the name is not available.");
        }

        var initials = Initials(name);
        return initials.Contains(normalizedPan[4])
            ? new CheckResult("PAN_NAME_INITIAL", CheckStage.Consistency, CheckOutcome.Pass, CheckSeverity.Info, "fullName", "The 5th character agrees with the initial of a name part.")
            : new CheckResult("PAN_NAME_INITIAL", CheckStage.Consistency, CheckOutcome.Warn, CheckSeverity.Warning, "fullName", "The 5th character does not match the initial of any name part (name order varies).");
    }

    private static HashSet<char> Initials(string name) =>
        name.Split([' ', '.', '-', '\''], StringSplitOptions.RemoveEmptyEntries).Where(t => char.IsLetter(t[0])).Select(t => char.ToUpperInvariant(t[0])).ToHashSet();
}
