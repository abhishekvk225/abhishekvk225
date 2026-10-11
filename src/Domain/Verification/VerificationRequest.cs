using System.Text.Json;
using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Verification;

/// <summary>
/// One document verification. Holds no personal data (docs/07 ADR-25): masked values and counts only. Owns the state machine
/// (<see cref="Transition"/>) and Rule V-1 (<see cref="RecordExternalResult"/>). Document, result and checks hang off it in strict-tenant tables.
/// </summary>
public sealed class VerificationRequest : AuditableEntity, ITenantOwned
{
    private readonly List<VerificationEvent> _events = [];
    private List<string> _reviewReasonCodes = [];

    private VerificationRequest()
    {
    }

    public Guid ClientId { get; set; }

    public VerificationType Type { get; private set; }

    public VerificationStatus Status { get; private set; }

    public StageState UploadState { get; private set; }

    public StageState ExtractionState { get; private set; }

    public StageState RulesState { get; private set; }

    public StageState ConsistencyState { get; private set; }

    public ExternalState ExternalState { get; private set; }

    public string? ExternalProviderId { get; private set; }

    public string? ExternalReference { get; private set; }

    public DateTime? ExternalCompletedAt { get; private set; }

    public bool ReviewRequired { get; private set; }

    /// <summary>JSON array of codes only (no messages, no values). Persisted as <c>ReviewReasonCodesJson</c>.</summary>
    public string ReviewReasonCodesJson
    {
        get => JsonSerializer.Serialize(_reviewReasonCodes);
        private set => _reviewReasonCodes = string.IsNullOrEmpty(value) ? [] : JsonSerializer.Deserialize<List<string>>(value) ?? [];
    }

    /// <summary>Computed on every mutation by <see cref="VerificationRules.ComputeAssurance"/>; there is no public way to set it.</summary>
    public AssuranceLevel Assurance { get; private set; }

    public string? ErrorCode { get; private set; }

    public SubmitSource Source { get; private set; }

    public VerificationActorType SubmittedByType { get; private set; }

    public Guid? SubmittedById { get; private set; }

    /// <summary>The caller's own reference. Documented as "do not put personal data here"; never logged.</summary>
    public string? ExternalRef { get; private set; }

    public string? ConsentReference { get; private set; }

    /// <summary>Server-derived (client|type|callerKey|documentSha|fields); unique per client when set.</summary>
    public string? IdempotencyKey { get; private set; }

    public byte[]? DocumentSha256 { get; private set; }

    public int? PageCount { get; private set; }

    public int CreditsCharged { get; private set; }

    public long? LicenseTransactionId { get; private set; }

    public int ProviderCreditsCharged { get; private set; }

    public long? ProviderLicenseTransactionId { get; private set; }

    public int Attempts { get; private set; }

    public int ProviderAttempts { get; private set; }

    public DateTime? NextAttemptAt { get; private set; }

    public Guid? LeaseToken { get; private set; }

    public DateTime? LeaseUntil { get; private set; }

    public DateTime? CancelRequestedAt { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    public string? IpAddress { get; private set; }

    public DateTime? StartedAt { get; private set; }

    public DateTime? CompletedAt { get; private set; }

    /// <summary>When the document / result are purged (retention).</summary>
    public DateTime? ExpiresAt { get; private set; }

    public DateTime? PurgedAt { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<string> ReviewReasonCodes => _reviewReasonCodes;

    public IReadOnlyList<VerificationEvent> Events => _events;

    public bool IsTerminal => VerificationRules.IsTerminal(Status);

    public static VerificationRequest Create(
        Guid id, Guid clientId, VerificationType type, SubmitSource source, VerificationActorType submittedByType, Guid? submittedById,
        string? externalRef, string? consentReference, string? idempotencyKey, byte[]? documentSha256, int? pageCount,
        string correlationId, string? ipAddress, DateTime now, DateTime? expiresAt)
    {
        var request = new VerificationRequest
        {
            ClientId = clientId,
            Type = type,
            Source = source,
            SubmittedByType = submittedByType,
            SubmittedById = submittedById,
            ExternalRef = externalRef,
            ConsentReference = consentReference,
            IdempotencyKey = idempotencyKey,
            DocumentSha256 = documentSha256,
            PageCount = pageCount,
            CorrelationId = correlationId is { Length: > 64 } c ? c[..64] : correlationId,
            IpAddress = ipAddress is { Length: > 45 } ip ? ip[..45] : ipAddress,
            Status = VerificationStatus.Queued,
            // A number-only PAN request has no file: nothing was uploaded and nothing will be extracted.
            UploadState = documentSha256 is null ? StageState.Skipped : StageState.Completed,
            NextAttemptAt = now,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };
        if (id != Guid.Empty)
        {
            request.Id = id;
        }

        request.RefreshAssurance();
        request.AppendEvent(null, VerificationStatus.Queued, "Uploaded", "SUBMITTED", submittedByType, submittedById, now);
        return request;
    }

    /// <summary>The only way the status changes. Any move not in <see cref="VerificationRules.CanTransition"/> throws.</summary>
    public void Transition(
        VerificationStatus to, DateTime now, string? stage = null, string? noteCode = null,
        VerificationActorType actorType = VerificationActorType.System, Guid? actorId = null)
    {
        if (!VerificationRules.CanTransition(Status, to))
        {
            throw new DomainException(VerificationDomainErrors.InvalidTransition, $"A verification cannot move from {Status} to {to}.");
        }

        var from = Status;
        Status = to;
        AppendEvent(from, to, stage, noteCode, actorType, actorId, now);
    }

    /// <summary>Queued to Processing: takes the lease and counts the attempt (attempts are counted at claim).</summary>
    public void Claim(Guid leaseToken, DateTime leaseUntil, DateTime now)
    {
        Transition(VerificationStatus.Processing, now, "Claim");
        Attempts++;
        LeaseToken = leaseToken;
        LeaseUntil = leaseUntil;
        NextAttemptAt = null;
        StartedAt ??= now;
    }

    /// <summary>
    /// Processing to Queued: lease expired, retryable error, or graceful-shutdown hand-back (<paramref name="countAttempt"/> false
    /// gives the attempt back).
    /// </summary>
    public void ReturnToQueue(DateTime nextAttemptAt, bool countAttempt, DateTime now, string noteCode)
    {
        Transition(VerificationStatus.Queued, now, null, noteCode);
        if (!countAttempt && Attempts > 0)
        {
            Attempts--;
        }

        NextAttemptAt = nextAttemptAt;
        ClearLease();
    }

    /// <summary>Records which document-stage flags ran. A flag can only advance from NotStarted; a finished stage is never rewritten (idempotent re-claim).</summary>
    public void RecordDocumentStage(StageState extracted, StageState rules, StageState consistency, IEnumerable<string> reviewReasonCodes)
    {
        EnsureProcessing();
        ExtractionState = Advance(ExtractionState, extracted);
        RulesState = Advance(RulesState, rules);
        ConsistencyState = Advance(ConsistencyState, consistency);
        foreach (var code in reviewReasonCodes)
        {
            AddReviewReason(code);
        }

        RefreshAssurance();
    }

    public void AddReviewReason(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        if (!_reviewReasonCodes.Contains(code, StringComparer.Ordinal))
        {
            _reviewReasonCodes.Add(code);
        }

        ReviewRequired = true;
    }

    /// <summary>The authoritative call is in flight (allowed from NotRequested or a retryable state).</summary>
    public void MarkExternalPending()
    {
        EnsureProcessing();
        EnsureExternalNotFinal();
        ExternalState = ExternalState.Pending;
        ProviderAttempts++;
        RefreshAssurance();
    }

    /// <summary>
    /// Records the authoritative outcome. <see cref="ExternalState.Verified"/> without a provider id AND a provider reference throws
    /// <c>VERIFIED_REQUIRES_PROVIDER</c> (Rule V-1). Definitive answers (Verified, NotVerified) cannot be overwritten.
    /// </summary>
    public void RecordExternalResult(ExternalState state, string? providerId, string? providerReference, DateTime now)
    {
        EnsureProcessing();
        if (state == ExternalState.Verified && (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(providerReference)))
        {
            throw new DomainException(VerificationDomainErrors.VerifiedRequiresProvider, "A verified result requires the provider id and the provider reference.");
        }

        EnsureExternalNotFinal();

        ExternalState = state;
        ExternalProviderId = string.IsNullOrWhiteSpace(providerId) ? null : Truncate(providerId.Trim(), 50);
        ExternalReference = string.IsNullOrWhiteSpace(providerReference) ? null : Truncate(providerReference.Trim(), 100);
        ExternalCompletedAt = state == ExternalState.Pending ? null : now;

        _reviewReasonCodes.RemoveAll(c => ReviewCodes.ProviderDerived.Contains(c, StringComparer.Ordinal));
        switch (state)
        {
            case ExternalState.NotVerified:
                _reviewReasonCodes.Add(ReviewCodes.ProviderNotVerified);
                break;
            case ExternalState.Inconclusive:
                _reviewReasonCodes.Add(ReviewCodes.ProviderInconclusive);
                break;
            case ExternalState.ProviderError or ExternalState.Timeout:
                _reviewReasonCodes.Add(ReviewCodes.ProviderUnavailable);
                break;
        }

        ReviewRequired = _reviewReasonCodes.Count > 0;
        RefreshAssurance();
    }

    public void RecordExtractionCharge(int credits, long? transactionId)
    {
        CreditsCharged = credits;
        LicenseTransactionId = transactionId;
    }

    public void RecordProviderCharge(int credits, long? transactionId)
    {
        ProviderCreditsCharged = credits;
        ProviderLicenseTransactionId = transactionId;
    }

    /// <summary>Processing to Completed, or to ReviewRequired when any review reason exists. Neither means "verified".</summary>
    public void Complete(DateTime now)
    {
        var to = ReviewRequired ? VerificationStatus.ReviewRequired : VerificationStatus.Completed;
        Transition(to, now);
        CompletedAt = now;
        ErrorCode = null;
        ClearLease();
    }

    public void Fail(string errorCode, DateTime now)
    {
        Transition(VerificationStatus.Failed, now, null, errorCode);
        ErrorCode = errorCode;
        CompletedAt = now;
        ClearLease();
    }

    /// <summary>Queued: cancelled at once. Processing: only the flag is set; the worker ends it with <see cref="Cancel"/> between stages.</summary>
    public void RequestCancel(DateTime now, VerificationActorType actorType, Guid? actorId)
    {
        switch (Status)
        {
            case VerificationStatus.Queued:
                Cancel(now, actorType, actorId);
                break;
            case VerificationStatus.Processing:
                CancelRequestedAt ??= now;
                break;
            default:
                throw new DomainException(VerificationDomainErrors.NotCancellable, "Only queued or processing verifications can be cancelled.");
        }
    }

    public void Cancel(DateTime now, VerificationActorType actorType = VerificationActorType.System, Guid? actorId = null)
    {
        Transition(VerificationStatus.Cancelled, now, null, VerificationRules.FailureCodes.Cancelled, actorType, actorId);
        ErrorCode = VerificationRules.FailureCodes.Cancelled;
        CompletedAt = now;
        ClearLease();
    }

    /// <summary>Queued longer than the allowed age; never charged.</summary>
    public void Expire(DateTime now)
    {
        Transition(VerificationStatus.Expired, now, null, VerificationRules.FailureCodes.Expired);
        ErrorCode = VerificationRules.FailureCodes.Expired;
        CompletedAt = now;
    }

    /// <summary>
    /// retry-external: Completed/ReviewRequired back to Processing for the provider stage only. Allowed when the external state is
    /// retryable and the data has not been purged.
    /// </summary>
    public void BeginExternalRetry(DateTime now, VerificationActorType actorType, Guid? actorId)
    {
        if (PurgedAt is not null || !VerificationRules.IsRetryable(ExternalState))
        {
            throw new DomainException(VerificationDomainErrors.NotRetryable, "The provider check cannot be retried for this verification.");
        }

        if (Status is not (VerificationStatus.Completed or VerificationStatus.ReviewRequired))
        {
            throw new DomainException(VerificationDomainErrors.NotRetryable, "Only a finished verification can be retried.");
        }

        Transition(VerificationStatus.Processing, now, "External", "EXTERNAL_RETRY", actorType, actorId);
        CompletedAt = null;
    }

    public void MarkPurged(DateTime now) => PurgedAt = now;

    private void EnsureProcessing()
    {
        if (Status != VerificationStatus.Processing)
        {
            throw new DomainException(VerificationDomainErrors.InvalidState, "Results can only be recorded while the verification is processing.");
        }
    }

    private void EnsureExternalNotFinal()
    {
        if (ExternalState is ExternalState.Verified or ExternalState.NotVerified)
        {
            throw new DomainException(VerificationDomainErrors.ExternalResultFinal, "A definitive provider answer cannot be replaced.");
        }
    }

    private static StageState Advance(StageState current, StageState next) =>
        current == StageState.NotStarted ? next : current;

    private void ClearLease()
    {
        LeaseToken = null;
        LeaseUntil = null;
    }

    private void RefreshAssurance() =>
        Assurance = VerificationRules.ComputeAssurance(ExtractionState, RulesState, ConsistencyState, ExternalState, ExternalProviderId, ExternalReference);

    private void AppendEvent(VerificationStatus? from, VerificationStatus to, string? stage, string? noteCode, VerificationActorType actorType, Guid? actorId, DateTime now) =>
        _events.Add(VerificationEvent.Create(ClientId, Id, from, to, stage, noteCode, actorType, actorId, now));

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
}
