using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Api;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Modules;
using NexaVerify.Application.Verification.Providers;
using NexaVerify.Application.Verification.Submission;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Query;

public interface IVerificationLifecycleService
{
    /// <summary>Queued: cancelled at once. Processing: asked to stop (the worker ends it between stages). Anything else: 409 VERIFICATION_NOT_CANCELLABLE.</summary>
    Task<Result<VerificationStatusDto>> CancelAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Re-runs only the authoritative stage of a finished request: it is re-queued and a worker claims it under a lease.</summary>
    Task<Result<VerificationAcceptedDto>> RetryExternalAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Immediate erasure of the document, extracted data and checks. A request that is still being processed must be cancelled first.</summary>
    Task<Result> EraseAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class VerificationLifecycleService : IVerificationLifecycleService
{
    private readonly ICurrentUser _user;
    private readonly IVerificationRepository _requests;
    private readonly IVerificationRetention _retention;
    private readonly IVerificationModuleRegistry _registry;
    private readonly IVerificationProviderCatalog _providers;
    private readonly IPermissionEvaluator _permissions;
    private readonly IModuleGate _gate;
    private readonly IAuditService _audit;
    private readonly IWebhookPublisher _webhooks;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;

    public VerificationLifecycleService(
        ICurrentUser user,
        IVerificationRepository requests,
        IVerificationRetention retention,
        IVerificationModuleRegistry registry,
        IVerificationProviderCatalog providers,
        IPermissionEvaluator permissions,
        IModuleGate gate,
        IAuditService audit,
        IWebhookPublisher webhooks,
        IUnitOfWork unitOfWork,
        TimeProvider time)
    {
        _user = user;
        _requests = requests;
        _retention = retention;
        _registry = registry;
        _providers = providers;
        _permissions = permissions;
        _gate = gate;
        _audit = audit;
        _webhooks = webhooks;
        _unitOfWork = unitOfWork;
        _time = time;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<Result<VerificationStatusDto>> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var request = await _requests.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return Error.NotFound("The verification was not found.");
        }

        if (await RequireSubmitPermissionAsync(request.Type, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var now = Now;
        var wasQueued = request.Status == VerificationStatus.Queued;
        try
        {
            request.RequestCancel(now, ActorKind(), _user.ActorId);
        }
        catch (DomainException ex)
        {
            return new Error(ErrorCodes.VerificationNotCancellable, ex.Message, ErrorType.Conflict);
        }

        if (wasQueued)
        {
            request.SetExpiresAt(now);
            _audit.Record(new AuditEntry(
                VerificationAuditActions.Cancelled, VerificationAuditActions.EntityType, id.ToString(), request.ClientId,
                NewValues: new { type = request.Type.ToString(), status = request.Status.ToString() }));
            await _webhooks.PublishAsync(
                request.ClientId, Domain.Api.WebhookEvents.VerificationCompleted,
                new
                {
                    requestId = request.Id, type = request.Type.ToString(), status = request.Status.ToString(), reviewRequired = request.ReviewRequired,
                    externalVerification = request.ExternalState.ToString(), assurance = request.Assurance.ToString(), creditsCharged = 0, balanceAfter = (int?)null,
                },
                cancellationToken).ConfigureAwait(false);
        }

        if (!await _requests.TrySaveAsync(cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(ErrorCodes.ConcurrencyConflict, "The verification changed while it was being cancelled. Please retry.");
        }

        if (wasQueued)
        {
            await _retention.EraseAsync(id, now, cancellationToken).ConfigureAwait(false); // nothing was delivered: nothing is kept
        }

        var fresh = await _requests.GetReadOnlyAsync(id, cancellationToken).ConfigureAwait(false);
        return VerificationDtoMapper.Status(fresh ?? request);
    }

    public async Task<Result<VerificationAcceptedDto>> RetryExternalAsync(Guid id, CancellationToken cancellationToken)
    {
        var request = await _requests.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return Error.NotFound("The verification was not found.");
        }

        if (await RequireSubmitPermissionAsync(request.Type, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        var module = await _gate.EnsureAvailableAsync(request.Type, cancellationToken).ConfigureAwait(false);
        if (module.IsFailure)
        {
            return module.Error!;
        }

        if (!_providers.HasProviderStage(request.Type) || !_providers.IsConfigured(request.Type))
        {
            return new Error(ErrorCodes.VerificationNotRetryable, "No authoritative provider is configured for this verification type.", ErrorType.Conflict);
        }

        try
        {
            request.RequeueForExternalRetry(Now, ActorKind(), _user.ActorId);
        }
        catch (DomainException ex)
        {
            return new Error(ErrorCodes.VerificationNotRetryable, ex.Message, ErrorType.Conflict);
        }

        _audit.Record(new AuditEntry(
            VerificationAuditActions.ExternalRetry, VerificationAuditActions.EntityType, id.ToString(), request.ClientId,
            NewValues: new { type = request.Type.ToString(), externalState = request.ExternalState.ToString() }));
        if (!await _requests.TrySaveAsync(cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(ErrorCodes.ConcurrencyConflict, "The verification changed at the same time. Please retry.");
        }

        return new VerificationAcceptedDto(
            request.Id, request.Type.ToString(), request.Status.ToString(), request.CreatedAt, VerificationSubmissionCore.StatusUrl(request.Id), 1, false);
    }

    public async Task<Result> EraseAsync(Guid id, CancellationToken cancellationToken)
    {
        var request = await _requests.GetReadOnlyAsync(id, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return Error.NotFound("The verification was not found.");
        }

        if (request.Status is VerificationStatus.Queued or VerificationStatus.Processing)
        {
            return Error.Conflict(ErrorCodes.Conflict, "Cancel the verification before erasing its data.");
        }

        await _retention.EraseAsync(id, Now, cancellationToken).ConfigureAwait(false);
        _audit.Record(new AuditEntry(
            VerificationAuditActions.Erased, VerificationAuditActions.EntityType, id.ToString(), request.ClientId,
            NewValues: new { type = request.Type.ToString() }));
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>cancel / retry need the submit permission of the request's own type (a bank-statement-only key cannot cancel a passport).</summary>
    private async Task<Error?> RequireSubmitPermissionAsync(VerificationType type, CancellationToken cancellationToken)
    {
        var descriptor = _registry.Find(type);
        if (descriptor is null || descriptor.SubmitPermissions.Count == 0)
        {
            return Error.NotFound("The verification was not found.");
        }

        foreach (var permission in descriptor.SubmitPermissions)
        {
            if (await _permissions.HasPermissionAsync(permission, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }

        return Error.Forbidden(ErrorCodes.Forbidden, "You do not have permission to change this kind of verification.");
    }

    private VerificationActorType ActorKind() => _user.ActorType == ActorType.ApiKey ? VerificationActorType.ApiKey : VerificationActorType.User;
}
