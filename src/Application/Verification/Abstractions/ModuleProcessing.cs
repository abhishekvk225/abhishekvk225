using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Abstractions;

/// <summary>A request claimed by the worker (lease taken). Processing runs inside the request's tenant scope.</summary>
public sealed record ClaimedRequest(Guid RequestId, Guid ClientId, VerificationType Type, Guid LeaseToken, int Attempt);

/// <summary>Read-only view of the request handed to module code. Holds no personal data.</summary>
public sealed record VerificationRequestSnapshot(
    Guid Id, Guid ClientId, VerificationType Type, string? ExternalRef, string CorrelationId, int Attempts,
    StageState ExtractionState, StageState RulesState, StageState ConsistencyState, ExternalState ExternalState);

public interface IVerificationProcessor
{
    /// <summary>Called by the worker for ONE claimed request; runs in the request's tenant scope.</summary>
    Task ProcessAsync(ClaimedRequest claim, CancellationToken cancellationToken);
}

/// <summary>The async pipeline of one module. Never throws for expected conditions.</summary>
public interface IModuleProcessor
{
    VerificationType Type { get; }

    /// <summary>Extracted + RulesChecked + ConsistencyChecked.</summary>
    Task<DocumentStageOutcome> ProcessDocumentAsync(ModuleContext context, CancellationToken cancellationToken);

    Task<ExternalStageOutcome> VerifyExternalAsync(ModuleContext context, CancellationToken cancellationToken);
}

/// <summary><c>SubmittedFields</c> hold personal data: the record's <c>ToString</c> never prints them.</summary>
public sealed record ModuleContext(
    VerificationRequestSnapshot Request, DocumentKind Kind, ReadOnlyMemory<byte> Document,
    IReadOnlyDictionary<string, string?> SubmittedFields, ExtractedData? Extracted, string CorrelationId)
{
    public override string ToString() => nameof(ModuleContext);
}

public sealed record DocumentStageOutcome(
    StageState Extracted, StageState Rules, StageState Consistency, ExtractedData? Data, IReadOnlyList<CheckResult> Checks,
    IReadOnlyList<ReviewReason> Reasons, string? FailureCode)
{
    public override string ToString() => nameof(DocumentStageOutcome);
}

public sealed record ExternalStageOutcome(
    ExternalState State, string? ProviderId, string? ProviderReference, IReadOnlyList<FieldMatch> FieldMatches, string? ReasonCode);

/// <summary><c>Message</c> is PII-free template text (counts and indices only), never a personal value.</summary>
public sealed record CheckResult(string Code, CheckStage Stage, CheckOutcome Outcome, CheckSeverity Severity, string? Field, string Message);

public sealed record FieldMatch(string Field, FieldMatchOutcome Outcome);

public sealed record ReviewReason(string Code, string Message);

/// <summary>Outcome of comparing two names. The score stays server-side; only the outcome is ever returned to a client.</summary>
public sealed record NameMatch(FieldMatchOutcome Outcome, decimal Score);

public interface INameMatcher
{
    NameMatch Compare(string? submitted, string? extracted);
}
