using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Api;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Billing;
using NexaVerify.Application.Verification.Providers;
using NexaVerify.Application.Verification.Submission;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Processing;

/// <summary>
/// Processes ONE claimed request in its tenant scope (the worker enters the scope). The work is a short loop of idempotent steps, each of
/// which re-reads the request, checks it still holds the lease, does its slow work WITHOUT a tracked row (so lease heartbeats cannot
/// invalidate it) and then commits in ONE transaction: stage result, checks, flags, ledger charge, status, audit and webhook together.
///
/// Credit policy (docs/07 §8, "C-5"): the extraction stage is charged when a completed pipeline result is stored (a review outcome is
/// charged, it is the product); the provider stage is charged only for a definitive answer (Verified, NotVerified). Failed, cancelled,
/// expired, unreadable and every provider failure (error, timeout, inconclusive, not configured) consume nothing. Ledger keys
/// <c>vr:{id:N}:x</c> / <c>:p</c> make a re-run impossible to double-charge.
/// </summary>
public sealed class VerificationProcessor : IVerificationProcessor
{
    private const int MaxStepsPerClaim = 6;
    private const int MaxCommitAttempts = 3;

    private readonly IVerificationRepository _requests;
    private readonly IVerificationDocumentStore _documents;
    private readonly IVerificationResultStore _results;
    private readonly IVerificationRetention _retention;
    private readonly IDocumentStorageService _storage;
    private readonly VerificationEnvelopeCodec _codec;
    private readonly IReadOnlyDictionary<VerificationType, IModuleProcessor> _modules;
    private readonly IVerificationMeter _meter;
    private readonly IVerificationProviderCatalog _providers;
    private readonly IPlatformCrypto _crypto;
    private readonly IAuditService _audit;
    private readonly IWebhookPublisher _webhooks;
    private readonly IUnitOfWork _unitOfWork;
    private readonly VerificationOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<VerificationProcessor> _logger;

    public VerificationProcessor(
        IVerificationRepository requests,
        IVerificationDocumentStore documents,
        IVerificationResultStore results,
        IVerificationRetention retention,
        IDocumentStorageService storage,
        VerificationEnvelopeCodec codec,
        IEnumerable<IModuleProcessor> modules,
        IVerificationMeter meter,
        IVerificationProviderCatalog providers,
        IPlatformCrypto crypto,
        IAuditService audit,
        IWebhookPublisher webhooks,
        IUnitOfWork unitOfWork,
        IOptions<VerificationOptions> options,
        TimeProvider time,
        ILogger<VerificationProcessor> logger)
    {
        _requests = requests;
        _documents = documents;
        _results = results;
        _retention = retention;
        _storage = storage;
        _codec = codec;
        _modules = modules.ToDictionary(m => m.Type);
        _meter = meter;
        _providers = providers;
        _crypto = crypto;
        _audit = audit;
        _webhooks = webhooks;
        _unitOfWork = unitOfWork;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task ProcessAsync(ClaimedRequest claim, CancellationToken cancellationToken)
    {
        for (var step = 0; step < MaxStepsPerClaim; step++)
        {
            StepResult result;
            try
            {
                result = await StepAsync(claim, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // the host is stopping: the worker hands the request back without counting the attempt
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Never log the message: a parser exception can quote document text. Type and ids only.
                _logger.LogError("Verification {RequestId} step failed ({ExceptionType})", claim.RequestId, ex.GetType().Name);
                _unitOfWork.ClearTracked();
                await RetryOrFailAsync(claim, VerificationRules.FailureCodes.ProcessingFailed, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (result == StepResult.Done)
            {
                return;
            }
        }

        _logger.LogWarning("Verification {RequestId} did not settle within {Steps} steps", claim.RequestId, MaxStepsPerClaim);
    }

    private enum StepResult
    {
        Continue = 0,
        Done,
    }

    private async Task<StepResult> StepAsync(ClaimedRequest claim, CancellationToken ct)
    {
        _unitOfWork.ClearTracked();
        var snapshot = await _requests.GetReadOnlyAsync(claim.RequestId, ct).ConfigureAwait(false);
        if (snapshot is null || snapshot.Status != VerificationStatus.Processing || snapshot.LeaseToken != claim.LeaseToken)
        {
            _logger.LogInformation("Verification {RequestId} is no longer held by this claim; leaving it", claim.RequestId);
            return StepResult.Done;
        }

        if (!_modules.TryGetValue(snapshot.Type, out var module))
        {
            await FailAsync(claim, VerificationRules.FailureCodes.ProcessingFailed, chargeFailedOutcome: false, ct).ConfigureAwait(false);
            return StepResult.Done;
        }

        if (snapshot.CancelRequestedAt is not null)
        {
            await CancelAsync(claim, snapshot, ct).ConfigureAwait(false);
            return StepResult.Done;
        }

        var documentStageDone = snapshot.ExtractionState != StageState.NotStarted || snapshot.RulesState != StageState.NotStarted;
        if (!documentStageDone)
        {
            return await DocumentStageAsync(claim, snapshot, module, ct).ConfigureAwait(false);
        }

        var externalDue = _providers.HasProviderStage(snapshot.Type)
            && (snapshot.ExternalState == ExternalState.NotRequested || VerificationRules.IsRetryable(snapshot.ExternalState));
        if (externalDue)
        {
            return await ExternalStageAsync(claim, snapshot, module, ct).ConfigureAwait(false);
        }

        // Everything ran (for example a re-claim after a crash between the last stage and the final status).
        await CommitAsync(claim, async (request, tok) =>
        {
            await FinishAsync(request, null, tok).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(tok).ConfigureAwait(false);
            return null;
        }, ct).ConfigureAwait(false);
        return StepResult.Done;
    }

    // ------------------------------------------------------------------------------------------------ document stage

    private async Task<StepResult> DocumentStageAsync(ClaimedRequest claim, VerificationRequest snapshot, IModuleProcessor module, CancellationToken ct)
    {
        var ticket = await _meter.PreflightAsync(claim.ClientId, snapshot.Type, includeProvider: false, ct).ConfigureAwait(false);
        if (ticket.IsFailure)
        {
            await FailAsync(claim, VerificationRules.FailureCodes.CreditsUnavailable, chargeFailedOutcome: false, ct).ConfigureAwait(false);
            return StepResult.Done;
        }

        var envelope = await LoadEnvelopeAsync(claim, ct).ConfigureAwait(false);
        if (envelope is null)
        {
            await FailAsync(claim, VerificationRules.FailureCodes.ProcessingFailed, chargeFailedOutcome: false, ct).ConfigureAwait(false);
            return StepResult.Done;
        }

        var kind = DocumentKind.Pdf;
        byte[] bytes = [];
        if (snapshot.UploadState == StageState.Completed)
        {
            var document = await _documents.GetAsync(claim.RequestId, ct).ConfigureAwait(false);
            var read = document is null ? null : await _storage.ReadAsync(claim.RequestId, ct).ConfigureAwait(false);
            if (document is null || read is null || read.IsFailure)
            {
                await FailAsync(claim, VerificationRules.FailureCodes.DocumentUnreadable, chargeFailedOutcome: false, ct).ConfigureAwait(false);
                return StepResult.Done;
            }

            kind = document.Kind;
            bytes = read.Value;
        }

        _unitOfWork.ClearTracked();
        DocumentStageOutcome outcome;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.Processing.TimeoutSeconds));
            var context = new ModuleContext(Snapshot(snapshot), kind, bytes, envelope.Submitted, null, snapshot.CorrelationId);
            outcome = await module.ProcessDocumentAsync(context, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Verification {RequestId} document stage timed out", claim.RequestId);
            await RetryOrFailAsync(claim, VerificationRules.FailureCodes.ProcessingTimeout, ct).ConfigureAwait(false);
            return StepResult.Done;
        }
        finally
        {
            Array.Clear(bytes);
        }

        if (outcome.FailureCode is not null || outcome.Extracted == StageState.Failed)
        {
            await FailAsync(claim, outcome.FailureCode ?? VerificationRules.FailureCodes.DocumentUnreadable, chargeFailedOutcome: true, ct).ConfigureAwait(false);
            return StepResult.Done;
        }

        var sealedData = await _codec.SealAsync(claim.ClientId, claim.RequestId, new SubmissionEnvelope(envelope.Submitted, outcome.Data), ct).ConfigureAwait(false);
        var (masked, hash) = ResultSupport.Identifier(outcome.Data, _crypto);
        var hasProviderStage = _providers.HasProviderStage(snapshot.Type);

        var state = await CommitAsync(claim, async (request, tok) =>
        {
            var charge = await _meter.ChargeStageAsync(claim.ClientId, claim.RequestId, request.Type, VerificationStage.Extraction, MeterOutcome.Success, tok).ConfigureAwait(false);
            if (charge.IsFailure)
            {
                return charge.Error; // refused before anything was written
            }

            var now = Now;
            var matches = ResultSupport.FieldMatchesFromChecks(outcome.Checks);
            var result = VerificationResult.Create(
                claim.ClientId, claim.RequestId, SubmissionEnvelope.CurrentVersion, sealedData, masked, hash, ResultSupport.ToJson(matches),
                now.AddDays(_options.Retention.ResultDays));
            await _results.ReplaceResultAsync(result, tok).ConfigureAwait(false);
            await _results.ReplaceChecksAsync(
                claim.RequestId, [CheckStage.Extraction, CheckStage.Rules, CheckStage.Consistency, CheckStage.External],
                ResultSupport.ToEntities(claim.ClientId, claim.RequestId, 0, outcome.Checks), tok).ConfigureAwait(false);

            request.RecordDocumentStage(outcome.Extracted, outcome.Rules, outcome.Consistency, outcome.Reasons.Select(r => r.Code));
            request.RecordExtractionCharge(charge.Value.Charged, charge.Value.TransactionId);
            if (!hasProviderStage)
            {
                await FinishAsync(request, charge.Value.RemainingBalance, tok).ConfigureAwait(false);
            }

            await _unitOfWork.SaveChangesAsync(tok).ConfigureAwait(false);
            return null;
        }, ct).ConfigureAwait(false);

        if (state.Kind == CommitKind.ChargeRefused)
        {
            // C-7: never deliver for free. The extracted data is not persisted and the document is erased.
            await FailAsync(claim, VerificationRules.FailureCodes.CreditsUnavailable, chargeFailedOutcome: false, ct).ConfigureAwait(false);
            return StepResult.Done;
        }

        return state.Kind == CommitKind.LostLease || !hasProviderStage ? StepResult.Done : StepResult.Continue;
    }

    // ----------------------------------------------------------------------------------------------- external stage

    private async Task<StepResult> ExternalStageAsync(ClaimedRequest claim, VerificationRequest snapshot, IModuleProcessor module, CancellationToken ct)
    {
        var configured = _providers.IsConfigured(snapshot.Type);
        var unfunded = false;
        ExternalStageOutcome outcome;

        if (!configured)
        {
            // No call is made and nothing can be charged. The honest answer is "not available".
            outcome = new ExternalStageOutcome(ExternalState.ProviderNotConfigured, null, null, [], ProviderReasonCodes.NotConfigured);
        }
        else
        {
            var ticket = await _meter.PreflightStageAsync(claim.ClientId, snapshot.Type, VerificationStage.Provider, ct).ConfigureAwait(false);
            if (ticket.IsFailure)
            {
                unfunded = true;
                outcome = new ExternalStageOutcome(ExternalState.NotRequested, null, null, [], ReviewCodes.ProviderStageUnfunded);
            }
            else
            {
                var envelope = await LoadEnvelopeAsync(claim, ct).ConfigureAwait(false);
                if (envelope is null)
                {
                    await FailAsync(claim, VerificationRules.FailureCodes.ProcessingFailed, chargeFailedOutcome: false, ct).ConfigureAwait(false);
                    return StepResult.Done;
                }

                _unitOfWork.ClearTracked();
                outcome = Sanitise(await CallProviderAsync(snapshot, module, envelope, ct).ConfigureAwait(false));
            }
        }

        var meterOutcome = outcome.State switch
        {
            ExternalState.Verified => MeterOutcome.Success,
            ExternalState.NotVerified => MeterOutcome.NoMatch,
            _ => (MeterOutcome?)null, // errors, timeouts, inconclusive and "not configured" are never billed
        };

        await CommitAsync(claim, async (request, tok) =>
        {
            StageCharge? charge = null;
            var withheld = unfunded;
            if (!withheld && meterOutcome is { } mo)
            {
                var charged = await _meter.ChargeStageAsync(claim.ClientId, claim.RequestId, request.Type, VerificationStage.Provider, mo, tok).ConfigureAwait(false);
                if (charged.IsFailure)
                {
                    withheld = true; // C-7: the provider answer is discarded rather than delivered for free
                }
                else
                {
                    charge = charged.Value;
                }
            }

            var now = Now;
            if (withheld)
            {
                request.AddReviewReason(ReviewCodes.ProviderStageUnfunded);
            }
            else
            {
                if (outcome.State != ExternalState.ProviderNotConfigured)
                {
                    request.MarkExternalPending();
                }

                request.RecordExternalResult(outcome.State, outcome.State == ExternalState.ProviderNotConfigured ? null : outcome.ProviderId, outcome.ProviderReference, now);
                if (charge is not null)
                {
                    request.RecordProviderCharge(charge.Charged, charge.TransactionId);
                }
            }

            var existing = await _results.ListChecksAsync(claim.RequestId, tok).ConfigureAwait(false);
            var nextOrdinal = existing.Count == 0 ? 0 : existing.Max(c => c.Ordinal) + 1;
            await _results.ReplaceChecksAsync(
                claim.RequestId, [CheckStage.External],
                ResultSupport.ToEntities(claim.ClientId, claim.RequestId, nextOrdinal, [ResultSupport.ExternalCheck(outcome.State, withheld)]), tok).ConfigureAwait(false);

            if (!withheld && outcome.FieldMatches.Count > 0 && await _results.GetResultAsync(claim.RequestId, tok).ConfigureAwait(false) is { } row)
            {
                var merged = ResultSupport.FromJson(row.FieldMatchesJson)
                    .Where(m => !m.Field.StartsWith(ResultSupport.ProviderFieldPrefix, StringComparison.Ordinal))
                    .Concat(outcome.FieldMatches.Select(m => new FieldMatch(ResultSupport.ProviderFieldPrefix + m.Field, m.Outcome)));
                row.SetFieldMatches(ResultSupport.ToJson(merged));
                await _results.ReplaceResultAsync(row, tok).ConfigureAwait(false);
            }

            await FinishAsync(request, charge?.RemainingBalance, tok).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(tok).ConfigureAwait(false);
            return null;
        }, ct).ConfigureAwait(false);

        return StepResult.Done;
    }

    /// <summary>
    /// Rule V-1 belt and braces: a processor already routes provider answers through the call guard, but if one ever returned
    /// <c>Verified</c> without a provider id and reference, or a state a provider may not give, it is downgraded to ProviderError here.
    /// </summary>
    private static ExternalStageOutcome Sanitise(ExternalStageOutcome outcome)
    {
        if (outcome.State == ExternalState.Verified && (string.IsNullOrWhiteSpace(outcome.ProviderId) || string.IsNullOrWhiteSpace(outcome.ProviderReference)))
        {
            return new ExternalStageOutcome(ExternalState.ProviderError, outcome.ProviderId, null, [], ProviderReasonCodes.ReferenceMissing);
        }

        return outcome.State is ExternalState.Pending or ExternalState.NotRequested
            ? new ExternalStageOutcome(ExternalState.ProviderError, outcome.ProviderId, null, [], ProviderReasonCodes.InvalidResult)
            : outcome;
    }

    private async Task<ExternalStageOutcome> CallProviderAsync(VerificationRequest snapshot, IModuleProcessor module, SubmissionEnvelope envelope, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.Processing.TimeoutSeconds));
            var context = new ModuleContext(Snapshot(snapshot), DocumentKind.Pdf, ReadOnlyMemory<byte>.Empty, envelope.Submitted, envelope.Extracted, snapshot.CorrelationId);
            return await module.VerifyExternalAsync(context, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Verification {RequestId} provider stage timed out", snapshot.Id);
            return new ExternalStageOutcome(ExternalState.Timeout, null, null, [], ProviderReasonCodes.Timeout);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            _logger.LogWarning("Verification {RequestId} provider stage failed ({ExceptionType})", snapshot.Id, ex.GetType().Name);
            return new ExternalStageOutcome(ExternalState.ProviderError, null, null, [], ProviderReasonCodes.Error);
        }
    }

    // ------------------------------------------------------------------------------------------------- terminal moves

    /// <summary>Processing to Completed / ReviewRequired, with the retention date, the audit row and the webhook event.</summary>
    private async Task FinishAsync(VerificationRequest request, int? balanceAfter, CancellationToken ct)
    {
        var now = Now;
        request.SetExpiresAt(now.AddDays(Math.Max(_options.Retention.ResultDays, _options.Retention.DocumentDays)));
        request.Complete(now);
        await RecordTerminalAsync(request, VerificationAuditActions.Completed, balanceAfter, ct).ConfigureAwait(false);
    }

    private async Task FailAsync(ClaimedRequest claim, string code, bool chargeFailedOutcome, CancellationToken ct)
    {
        var state = await CommitAsync(claim, async (request, tok) =>
        {
            if (chargeFailedOutcome)
            {
                // Honours an operator-set "charge every attempt" policy; with the default policy a failed outcome is free. A refused
                // charge cannot change the answer, so it is ignored.
                _ = await _meter.ChargeStageAsync(claim.ClientId, claim.RequestId, request.Type, VerificationStage.Extraction, MeterOutcome.Failed, tok).ConfigureAwait(false);
            }

            var now = Now;
            request.Fail(code, now);
            request.SetExpiresAt(now);
            await RecordTerminalAsync(request, VerificationAuditActions.Failed, null, tok).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(tok).ConfigureAwait(false);
            return null;
        }, ct).ConfigureAwait(false);

        if (state.Kind == CommitKind.Committed)
        {
            await EraseAsync(claim, ct).ConfigureAwait(false);
        }
    }

    private async Task CancelAsync(ClaimedRequest claim, VerificationRequest snapshot, CancellationToken ct)
    {
        var delivered = snapshot.ExtractionState != StageState.NotStarted || snapshot.RulesState != StageState.NotStarted;
        var state = await CommitAsync(claim, async (request, tok) =>
        {
            var now = Now;
            request.Cancel(now);
            request.SetExpiresAt(delivered ? now.AddDays(_options.Retention.ResultDays) : now);
            await RecordTerminalAsync(request, VerificationAuditActions.Cancelled, null, tok).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(tok).ConfigureAwait(false);
            return null;
        }, ct).ConfigureAwait(false);

        // A stage that was already delivered (and charged) keeps its result; otherwise nothing is kept.
        if (state.Kind == CommitKind.Committed && !delivered)
        {
            await EraseAsync(claim, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Retryable fault or timeout: back to the queue with backoff while attempts remain, otherwise Failed.</summary>
    private async Task RetryOrFailAsync(ClaimedRequest claim, string code, CancellationToken ct)
    {
        var failed = false;
        var state = await CommitAsync(claim, async (request, tok) =>
        {
            var now = Now;
            if (request.Attempts >= _options.Processing.MaxAttempts)
            {
                request.Fail(code, now);
                request.SetExpiresAt(now);
                await RecordTerminalAsync(request, VerificationAuditActions.Failed, null, tok).ConfigureAwait(false);
                failed = true;
            }
            else
            {
                var backoff = _options.Processing.BackoffSeconds;
                var seconds = backoff[Math.Clamp(request.Attempts - 1, 0, backoff.Length - 1)];
                var jitter = Random.Shared.NextDouble() * 0.1 * seconds;
                request.ReturnToQueue(now.AddSeconds(seconds + jitter), countAttempt: true, now, code);
            }

            await _unitOfWork.SaveChangesAsync(tok).ConfigureAwait(false);
            return null;
        }, ct).ConfigureAwait(false);

        if (state.Kind == CommitKind.Committed && failed)
        {
            await EraseAsync(claim, ct).ConfigureAwait(false);
        }
    }

    private async Task EraseAsync(ClaimedRequest claim, CancellationToken ct)
    {
        try
        {
            await _retention.EraseAsync(claim.RequestId, Now, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // The retention sweeper removes it at ExpiresAt (set to now for these requests) if this attempt failed.
            _logger.LogWarning("Could not erase the data of verification {RequestId} ({ExceptionType})", claim.RequestId, ex.GetType().Name);
        }
    }

    private async Task RecordTerminalAsync(VerificationRequest request, string action, int? balanceAfter, CancellationToken ct)
    {
        _audit.Record(new AuditEntry(
            action, VerificationAuditActions.EntityType, request.Id.ToString(), request.ClientId,
            NewValues: new
            {
                type = request.Type.ToString(),
                status = request.Status.ToString(),
                reviewRequired = request.ReviewRequired,
                externalVerification = request.ExternalState.ToString(),
                creditsCharged = request.CreditsCharged + request.ProviderCreditsCharged,
                errorCode = request.ErrorCode,
            }));

        // Published inside the completing transaction (outbox semantics, like recognitions). Ids, status and credits only.
        await _webhooks.PublishAsync(
            request.ClientId, Domain.Api.WebhookEvents.VerificationCompleted,
            new
            {
                requestId = request.Id,
                type = request.Type.ToString(),
                status = request.Status.ToString(),
                reviewRequired = request.ReviewRequired,
                externalVerification = request.ExternalState.ToString(),
                assurance = request.Assurance.ToString(),
                creditsCharged = request.CreditsCharged + request.ProviderCreditsCharged,
                balanceAfter,
            },
            ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------------ plumbing

    private enum CommitKind
    {
        Committed = 0,
        LostLease,
        ChargeRefused,
    }

    private readonly record struct CommitState(CommitKind Kind, Error? Error);

    /// <summary>
    /// Loads the request fresh and tracked, checks the lease is still ours, and runs <paramref name="work"/> inside one transaction.
    /// <paramref name="work"/> returns an error only when it refused BEFORE writing anything (a refused charge). A concurrent change of the
    /// row (a cancel request, a heartbeat) rolls the whole transaction back, charge included, and the commit is retried on fresh state.
    /// </summary>
    private async Task<CommitState> CommitAsync(ClaimedRequest claim, Func<VerificationRequest, CancellationToken, Task<Error?>> work, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxCommitAttempts; attempt++)
        {
            _unitOfWork.ClearTracked();
            var request = await _requests.GetAsync(claim.RequestId, ct).ConfigureAwait(false);
            if (request is null || request.Status != VerificationStatus.Processing || request.LeaseToken != claim.LeaseToken)
            {
                return new CommitState(CommitKind.LostLease, null);
            }

            try
            {
                var error = await _unitOfWork.ExecuteInTransactionAsync(tok => work(request, tok), ct).ConfigureAwait(false);
                return error is null ? new CommitState(CommitKind.Committed, null) : new CommitState(CommitKind.ChargeRefused, error);
            }
            catch (ConcurrencyConflictException)
            {
                // someone changed the row between the load and the save; reload and decide again
            }
        }

        _unitOfWork.ClearTracked();
        return new CommitState(CommitKind.LostLease, null);
    }

    private async Task<SubmissionEnvelope?> LoadEnvelopeAsync(ClaimedRequest claim, CancellationToken ct)
    {
        var row = await _results.GetResultAsync(claim.RequestId, ct).ConfigureAwait(false);
        return row is null ? null : await _codec.OpenAsync(claim.ClientId, claim.RequestId, row.DataEnc, ct).ConfigureAwait(false);
    }

    private static VerificationRequestSnapshot Snapshot(VerificationRequest r) => new(
        r.Id, r.ClientId, r.Type, r.ExternalRef, r.CorrelationId, r.Attempts, r.ExtractionState, r.RulesState, r.ConsistencyState, r.ExternalState);
}
