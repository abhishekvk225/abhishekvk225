using NexaVerify.Application.Common;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Parsing;

/// <summary>Helpers shared by the three module processors.</summary>
public static class ProcessorSupport
{
    public static ParseOptions ParseOptionsFor(VerificationOptions o, VerificationType type) =>
        new(o.Upload.MaxPages.For(type), o.Ocr.Tesseract.Languages, o.Ocr.Dpi, o.Ocr.MinTextCharsPerPage);

    /// <summary>Failed extraction: nothing after it ran, so Rules and Consistency stay NotStarted.</summary>
    public static DocumentStageOutcome Failed(string code) =>
        new(StageState.Failed, StageState.NotStarted, StageState.NotStarted, null, [], [], code);

    public static string FailureCodeFor(Error error) => error.Code == ErrorCodes.OcrUnavailable
        ? VerificationRules.FailureCodes.OcrUnavailable
        : VerificationRules.FailureCodes.DocumentUnreadable;

    /// <summary>A Fail, or a Warn with Review severity, becomes a review reason (codes and PII-free messages only).</summary>
    public static List<ReviewReason> ReasonsFrom(IEnumerable<CheckResult> checks) =>
        checks.Where(c => c.Outcome == CheckOutcome.Fail || (c.Outcome == CheckOutcome.Warn && c.Severity == CheckSeverity.Review))
            .Select(c => new ReviewReason(c.Code, c.Message)).ToList();

    public static CheckOutcome ToCheckOutcome(FieldMatchOutcome outcome) => outcome switch
    {
        FieldMatchOutcome.Match => CheckOutcome.Pass,
        FieldMatchOutcome.PartialMatch => CheckOutcome.Warn,
        FieldMatchOutcome.Mismatch => CheckOutcome.Fail,
        _ => CheckOutcome.NotEvaluated,
    };

    /// <summary>
    /// A submitted-vs-extracted comparison expressed as a check: the code identifies the field, <c>Field</c> names it and the message
    /// starts with the <see cref="FieldMatchOutcome"/> name so callers can derive the field match without a score or a value.
    /// </summary>
    public static CheckResult FieldCheck(string code, string field, FieldMatchOutcome outcome) => new(
        code, CheckStage.Consistency, ToCheckOutcome(outcome),
        outcome is FieldMatchOutcome.Mismatch or FieldMatchOutcome.PartialMatch ? CheckSeverity.Review : CheckSeverity.Info, field,
        $"{outcome}: submitted and extracted value of this field compared.");

    public static CheckResult ConfidenceCheck(string code, DocumentText? text, VerificationOptions o) =>
        text?.MeanConfidence is { } c && c < o.Ocr.MinMeanConfidence
            ? new CheckResult(code, CheckStage.Rules, CheckOutcome.Warn, CheckSeverity.Review, null, "Text recognition confidence is low; review the extracted fields.")
            : new CheckResult(code, CheckStage.Rules, CheckOutcome.Pass, CheckSeverity.Info, null, "Text recognition confidence is adequate or not applicable.");
}
