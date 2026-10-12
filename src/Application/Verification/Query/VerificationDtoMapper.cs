using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Pan;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Query;

/// <summary>
/// Request state to wire shapes. Everything that reaches a client is built here, in one place: no score, no full identifier, no
/// provider payload, and no wording that calls anything genuine. Only <c>External.State = Verified</c> with a provider id and reference
/// is positive (Rule V-1).
/// </summary>
internal static class VerificationDtoMapper
{
    public const string Disclaimer =
        "This report records what was read from the document and which checks ran. It does not certify that the document is genuine unless External verification shows Verified by a named provider.";

    public static VerificationStagesDto Stages(VerificationRequest r) => new(
        r.UploadState.ToString(), r.ExtractionState.ToString(), r.RulesState.ToString(), r.ConsistencyState.ToString(), r.ExternalState.ToString(), r.ExternalProviderId);

    public static VerificationStatusDto Status(VerificationRequest r) => new(
        r.Id, r.Type.ToString(), r.Status.ToString(), Stages(r), r.ReviewRequired, r.Assurance.ToString(), r.ErrorCode, ErrorMessage(r.ErrorCode),
        r.CreatedAt, r.StartedAt, r.CompletedAt, r.CreditsCharged + r.ProviderCreditsCharged, r.CorrelationId);

    public static string? ErrorMessage(string? code) => code switch
    {
        null => null,
        VerificationRules.FailureCodes.DocumentUnreadable => "The document could not be read.",
        VerificationRules.FailureCodes.OcrUnavailable => "Text recognition is not available for this document right now.",
        VerificationRules.FailureCodes.ProcessingFailed => "Processing failed.",
        VerificationRules.FailureCodes.ProcessingTimeout => "Processing did not finish in time.",
        VerificationRules.FailureCodes.CreditsUnavailable => "There were not enough credits to complete this verification.",
        VerificationRules.FailureCodes.Cancelled => "The verification was cancelled.",
        VerificationRules.FailureCodes.Expired => "The verification waited too long to be processed.",
        _ => "The verification did not complete.",
    };

    public static VerificationListItemDto ListItem(VerificationListRow r) => new(
        r.Id, r.Type.ToString(), r.Status.ToString(), r.ReviewRequired, r.ExternalState.ToString(), r.Assurance.ToString(), r.IdentifierMasked, r.ExternalRef,
        r.CreatedAt, r.CompletedAt, r.CreditsCharged + r.ProviderCreditsCharged);

    public static VerificationResultDto Result(
        VerificationRequest r, VerificationDocument? document, IReadOnlyList<VerificationCheck> checks, SubmissionData? data, VerificationOptions options, bool includeCredits, DateOnly today)
    {
        var checkDtos = checks.Select(c => new VerificationCheckDto(c.Code, c.Stage.ToString(), c.Outcome.ToString(), c.Severity.ToString(), c.Field, c.Message)).ToList();
        var summary = new VerificationSummaryDto(
            checks.Count(c => c.Outcome == CheckOutcome.Pass), checks.Count(c => c.Outcome == CheckOutcome.Warn),
            checks.Count(c => c.Outcome == CheckOutcome.Fail), checks.Count(c => c.Outcome == CheckOutcome.NotEvaluated));

        var reasons = r.ReviewReasonCodes.Select(code => new VerificationReviewReasonDto(code, ReasonMessage(code, checks))).ToList();
        var matches = (data?.FieldMatches ?? []).Select(m => new VerificationFieldMatchDto(m.Field, m.Outcome.ToString())).ToList();
        var documentDto = document is null && r.UploadState != StageState.Completed
            ? null
            : new VerificationDocumentDto(
                document?.Kind.ToString() ?? string.Empty, document?.PageCount ?? r.PageCount ?? 0, document?.SizeBytes ?? 0, r.PurgedAt is not null || document?.PurgedAt is not null);

        VerificationCreditsDto? credits = includeCredits
            ? new VerificationCreditsDto(new StageCreditDto(r.CreditsCharged, r.LicenseTransactionId), new StageCreditDto(r.ProviderCreditsCharged, r.ProviderLicenseTransactionId))
            : null;

        return new VerificationResultDto(
            r.Id, r.Type.ToString(), r.Status.ToString(), Stages(r), r.ReviewRequired, r.Assurance.ToString(), r.ErrorCode, ErrorMessage(r.ErrorCode), r.CreatedAt,
            r.CorrelationId, r.ExternalRef, documentDto, summary, checkDtos, reasons, matches, External(r), Extracted(data?.Extracted, options, today), credits,
            new VerificationProcessingDto(r.StartedAt, r.CompletedAt, r.Attempts));
    }

    public static VerificationExternalDto External(VerificationRequest r) =>
        new(r.ExternalState.ToString(), r.ExternalProviderId, r.ExternalReference, r.ExternalCompletedAt, ExternalMessage(r.ExternalState));

    public static string? ExternalMessage(ExternalState state) => state switch
    {
        ExternalState.NotRequested => "No authoritative verification was requested or performed.",
        ExternalState.ProviderNotConfigured => "Official verification is not available for this account.",
        ExternalState.Pending => "Official verification is in progress.",
        ExternalState.Verified => "The authoritative provider confirmed the details.",
        ExternalState.NotVerified => "The provider could not confirm these details.",
        ExternalState.Inconclusive => "The provider's answer was inconclusive.",
        ExternalState.ProviderError or ExternalState.Timeout => "Official verification could not be completed. You can retry.",
        _ => null,
    };

    private static string ReasonMessage(string code, IReadOnlyList<VerificationCheck> checks)
    {
        if (checks.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.Ordinal) && c.Outcome is CheckOutcome.Fail or CheckOutcome.Warn) is { } check)
        {
            return check.Message;
        }

        return code switch
        {
            ReviewCodes.ProviderNotVerified => "The provider could not confirm these details.",
            ReviewCodes.ProviderInconclusive => "The provider's answer was inconclusive.",
            ReviewCodes.ProviderUnavailable => "Official verification could not be completed.",
            ReviewCodes.ProviderStageUnfunded => "The provider check was not performed because there were not enough credits.",
            _ => "A check raised a review signal.",
        };
    }

    public static VerificationExtractedDto? Extracted(ExtractedData? data, VerificationOptions options, DateOnly today)
    {
        var personal = options.Result.IncludeExtractedPersonalFields;
        return data switch
        {
            BankStatementData b => new VerificationExtractedDto(Bank(b, options, personal), null, null),
            PanData p => new VerificationExtractedDto(null, Pan(p, personal), null),
            PassportData p => new VerificationExtractedDto(null, null, Passport(p, personal, today)),
            _ => null,
        };
    }

    private static BankStatementExtractedDto Bank(BankStatementData b, VerificationOptions options, bool personal)
    {
        var inline = Math.Max(0, options.Result.InlineTransactions);
        return new BankStatementExtractedDto(
            b.BankName, personal ? b.AccountHolderName : null, b.AccountNumberMasked, b.Currency, b.PeriodFrom, b.PeriodTo, b.OpeningBalance, b.ClosingBalance,
            b.Transactions.Count, b.Transactions.Sum(t => t.Credit ?? 0m), b.Transactions.Sum(t => t.Debit ?? 0m), b.TextSource.ToString(), b.RuleSetId,
            b.Transactions.Take(inline).Select(Transaction).ToList(), b.Transactions.Count > inline);
    }

    public static BankTransactionDto Transaction(BankTransaction t) =>
        new(t.Index, t.Date, t.ValueDate, t.Description, t.Reference, t.Debit, t.Credit, t.Balance);

    private static PanExtractedDto Pan(PanData p, bool personal) => new(
        PanRules.Mask(p.PanNumber), p.HolderTypeCode,
        p.HolderTypeCode is { } code && PanRules.HolderTypeNames.TryGetValue(code, out var name) ? name : null,
        personal ? p.NameOnCard : null, personal ? p.FatherName : null, personal ? p.DateOfBirth : null);

    private static PassportExtractedDto Passport(PassportData p, bool personal, DateOnly today) => new(
        MaskedIdentifier.From(p.PassportNumber, 3).Value, p.IssuingCountry, p.Nationality, personal ? p.Surname : null, personal ? p.GivenNames : null,
        personal ? p.DateOfBirth : null, p.Sex, p.DateOfIssue, p.DateOfExpiry, p.DateOfExpiry is { } expiry && expiry < today,
        new MrzDto(p.MrzPresent, p.MrzFormat, p.MrzPresent
            ? new MrzChecksDto(State(p.MrzChecks.DocumentNumber), State(p.MrzChecks.DateOfBirth), State(p.MrzChecks.DateOfExpiry), State(p.MrzChecks.PersonalNumber), State(p.MrzChecks.Composite))
            : null));

    private static string State(MrzCheckState s) => s switch
    {
        MrzCheckState.Valid => "Valid",
        MrzCheckState.Invalid => "Invalid",
        _ => "NotPresent",
    };
}

/// <summary>The decrypted contents of a request's result row: extracted data and the field-level outcomes (outcomes only, no values).</summary>
internal sealed record SubmissionData(ExtractedData? Extracted, IReadOnlyList<FieldMatch> FieldMatches);
