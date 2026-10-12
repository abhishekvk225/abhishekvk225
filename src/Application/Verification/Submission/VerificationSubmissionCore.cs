using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Billing;
using NexaVerify.Application.Verification.Modules;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Application.Verification.Providers;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Submission;

/// <summary>The per-type part of a submission: which fields were supplied and whether a file is mandatory.</summary>
public sealed record SubmissionPlan(
    VerificationType Type, string? ExternalRef, string? ConsentReference, IReadOnlyDictionary<string, string?> Fields, SubmissionDocumentRule DocumentRule)
{
    public override string ToString() => nameof(SubmissionPlan);
}

public enum SubmissionDocumentRule
{
    /// <summary>A file is mandatory (bank statements, passports).</summary>
    Required = 0,

    /// <summary>A file, or the identifier submitted as a field (PAN number-only mode).</summary>
    DocumentOrNumber,
}

/// <summary>
/// The one submission pipeline shared by the three modules. Order, and nothing is stored or charged before step 7:
/// client principal, module gate, document presence and size, idempotency replay, queue cap, credit pre-flight, document inspection,
/// then ONE transaction that inserts the request (Queued), the encrypted document, the encrypted submitted fields, the event and the
/// audit row. No credit is deducted at accept time (docs/07 §8, decision C-1).
/// </summary>
public sealed class VerificationSubmissionCore
{
    public const int MaxIdempotencyKeyLength = 100;

    private readonly ICurrentUser _user;
    private readonly IRequestInfo _request;
    private readonly IModuleGate _gate;
    private readonly IDocumentInspector _inspector;
    private readonly IDocumentStorageService _storage;
    private readonly VerificationEnvelopeCodec _codec;
    private readonly IVerificationRepository _requests;
    private readonly IVerificationMeter _meter;
    private readonly IVerificationProviderCatalog _providers;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly VerificationOptions _options;
    private readonly TimeProvider _time;

    public VerificationSubmissionCore(
        ICurrentUser user,
        IRequestInfo request,
        IModuleGate gate,
        IDocumentInspector inspector,
        IDocumentStorageService storage,
        VerificationEnvelopeCodec codec,
        IVerificationRepository requests,
        IVerificationMeter meter,
        IVerificationProviderCatalog providers,
        IAuditService audit,
        IUnitOfWork unitOfWork,
        IOptions<VerificationOptions> options,
        TimeProvider time)
    {
        _user = user;
        _request = request;
        _gate = gate;
        _inspector = inspector;
        _storage = storage;
        _codec = codec;
        _requests = requests;
        _meter = meter;
        _providers = providers;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _options = options.Value;
        _time = time;
    }

    public async Task<Result<VerificationAcceptedDto>> SubmitAsync(
        SubmissionPlan plan, DocumentUpload? upload, string? idempotencyKey, CancellationToken cancellationToken)
    {
        // The tenant is the credential's, never the request's. Platform staff have no client and cannot submit.
        if (_user is not { IsAuthenticated: true, IsPlatformUser: false, ClientId: { } clientId })
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Document verification is only available to client accounts.");
        }

        var gate = await _gate.EnsureAvailableAsync(plan.Type, cancellationToken).ConfigureAwait(false);
        if (gate.IsFailure)
        {
            return gate.Error!;
        }

        if (upload is { Content.Length: 0 })
        {
            upload = null; // an empty file part is "no file"
        }

        if (idempotencyKey is { Length: > MaxIdempotencyKeyLength })
        {
            return Error.Validation("Idempotency-Key must be at most 100 characters.");
        }

        if (CheckDocument(plan, upload) is { } documentError)
        {
            return documentError;
        }

        var sha = upload is null ? null : SHA256.HashData(upload.Content);
        var derivedKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : DeriveKey(clientId, plan, idempotencyKey.Trim(), sha);

        if (derivedKey is not null && await _requests.FindByIdempotencyKeyAsync(derivedKey, cancellationToken).ConfigureAwait(false) is { } replay)
        {
            return Accepted(replay, replayed: true);
        }

        if (await _requests.CountOpenAsync(cancellationToken).ConfigureAwait(false) >= _options.Limits.MaxQueuedPerClient)
        {
            return Error.TooManyRequests(ErrorCodes.VerificationQueueFull, "Too many verifications are waiting to be processed. Retry shortly.");
        }

        var includeProvider = _providers.HasProviderStage(plan.Type) && _providers.IsConfigured(plan.Type);
        var gateCredits = await _meter.PreflightAsync(clientId, plan.Type, includeProvider, cancellationToken).ConfigureAwait(false);
        if (gateCredits.IsFailure)
        {
            return gateCredits.Error!;
        }

        DocumentInspection? inspection = null;
        if (upload is not null)
        {
            var inspected = await _inspector.InspectAsync(upload, _options.UploadPolicyFor(plan.Type), cancellationToken).ConfigureAwait(false);
            if (inspected.IsFailure)
            {
                return inspected.Error!;
            }

            inspection = inspected.Value;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var requestId = Guid.CreateVersion7();
        var retention = _options.Retention;
        var documentPurgeAfter = now.AddDays(retention.DocumentDays);
        var expiresAt = now.AddDays(Math.Max(retention.DocumentDays, retention.ResultDays));

        SealedDocument? sealedDocument = null;
        if (upload is not null && inspection is not null)
        {
            sealedDocument = await _storage.SealAsync(clientId, requestId, upload, inspection, documentPurgeAfter, cancellationToken).ConfigureAwait(false);
        }

        var envelope = new SubmissionEnvelope(plan.Fields, null);
        var sealedFields = await _codec.SealAsync(clientId, requestId, envelope, cancellationToken).ConfigureAwait(false);
        var submission = VerificationResult.Create(clientId, requestId, SubmissionEnvelope.CurrentVersion, sealedFields, null, null, "[]", expiresAt);

        var actorType = _user.ActorType == ActorType.ApiKey ? VerificationActorType.ApiKey : VerificationActorType.User;
        var request = VerificationRequest.Create(
            requestId, clientId, plan.Type, _user.ActorType == ActorType.ApiKey ? SubmitSource.Api : SubmitSource.Portal, actorType, _user.ActorId,
            Clean(plan.ExternalRef), Clean(plan.ConsentReference), derivedKey, inspection?.Sha256, inspection?.PageCount,
            _request.CorrelationId ?? Guid.NewGuid().ToString("N"), _request.IpAddress, now, expiresAt);

        _audit.Record(new AuditEntry(
            VerificationAuditActions.Submitted, VerificationAuditActions.EntityType, requestId.ToString(), clientId,
            NewValues: new { type = plan.Type.ToString(), source = request.Source.ToString(), hasDocument = upload is not null }));

        var outcome = await _requests.CreateIdempotentAsync(request, sealedDocument?.Document, sealedDocument?.Blob, cancellationToken, submission).ConfigureAwait(false);
        if (!outcome.Created)
        {
            _unitOfWork.ClearTracked(); // the staged audit row of the losing attempt must not be saved by a later call
            return Accepted(outcome.Request, replayed: true);
        }

        return Accepted(outcome.Request, replayed: false);
    }

    /// <summary>The identical-request fingerprint: the same caller key with a different document or different fields is a different request.</summary>
    public static string DeriveKey(Guid clientId, SubmissionPlan plan, string callerKey, byte[]? documentSha)
    {
        var fields = string.Join('\u001f', plan.Fields.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value?.Trim()));
        var material = $"{clientId:N}|{plan.Type}|{callerKey}|{(documentSha is null ? "-" : Convert.ToHexString(documentSha))}|{fields}|{plan.ExternalRef?.Trim()}|{plan.ConsentReference?.Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private Error? CheckDocument(SubmissionPlan plan, DocumentUpload? upload)
    {
        if (upload is null)
        {
            if (plan.DocumentRule != SubmissionDocumentRule.DocumentOrNumber)
            {
                return new Error(ErrorCodes.DocumentRequired, "A document file is required (form field 'document').", ErrorType.Validation);
            }

            return plan.Fields.TryGetValue(Parsing.SubmittedFieldKeys.PanNumber, out var number) && !string.IsNullOrWhiteSpace(number)
                ? null
                : Error.Validation(
                    "Upload a document or supply panNumber.",
                    new Dictionary<string, string[]> { ["document"] = ["Upload a document or supply panNumber."] });
        }

        return upload.Content.Length > _options.Upload.MaxFileBytes
            ? new Error(ErrorCodes.DocumentTooLarge, "The document is larger than the allowed size.", ErrorType.PayloadTooLarge)
            : null;
    }

    private VerificationAcceptedDto Accepted(VerificationRequest request, bool replayed) => new(
        request.Id, request.Type.ToString(), request.Status.ToString(), request.CreatedAt, StatusUrl(request.Id), Math.Max(1, _options.Worker.PollSeconds), replayed);

    public static string StatusUrl(Guid requestId) => $"/api/v1/verifications/{requestId}/status";

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
