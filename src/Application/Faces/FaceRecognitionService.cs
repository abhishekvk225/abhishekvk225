using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Licensing;
using NexaVerify.Application.Persistence;
using NexaVerify.Application.Tenancy;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Faces;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Faces;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.Faces;

public interface IFaceRecognitionService
{
    Task<Result<EnrollFaceResponse>> EnrollAsync(EnrollFaceRequest request, byte[] image, string? idempotencyKey, CancellationToken cancellationToken);

    Task<Result<VerifyFaceResponse>> VerifyAsync(VerifyFaceRequest request, byte[] image, string? idempotencyKey, CancellationToken cancellationToken);

    Task<Result<IdentifyFaceResponse>> IdentifyAsync(IdentifyFaceRequest request, byte[] image, string? idempotencyKey, CancellationToken cancellationToken);

    Task<Result<DetectFaceResponse>> DetectAsync(byte[] image, CancellationToken cancellationToken);
}

/// <summary>
/// Orchestrates every recognition call in the same order: replay check → licence pre-flight (before any image work) → image
/// validation → engine → ONE transaction that charges the licence and records the attempt. A failed charge withholds the
/// result (nothing is persisted), and the idempotency key is derived on the server from client, operation and image, so a
/// caller-supplied key can never be used to skip billing for a different request.
/// </summary>
public sealed class FaceRecognitionService : IFaceRecognitionService
{
    private readonly ICurrentUser _currentUser;
    private readonly IRequestInfo _request;
    private readonly IClientSettingsService _settings;
    private readonly ILicenseMeteringService _metering;
    private readonly IImageProcessor _images;
    private readonly IFaceEngine _engine;
    private readonly ITemplateIndex _index;
    private readonly IEmbeddingCodec _codec;
    private readonly IClientEncryption _encryption;
    private readonly IFaceRepository _faces;
    private readonly IRecognitionRepository _requests;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;
    private readonly ILogger<FaceRecognitionService> _logger;

    public FaceRecognitionService(
        ICurrentUser currentUser,
        IRequestInfo request,
        IClientSettingsService settings,
        ILicenseMeteringService metering,
        IImageProcessor images,
        IFaceEngine engine,
        ITemplateIndex index,
        IEmbeddingCodec codec,
        IClientEncryption encryption,
        IFaceRepository faces,
        IRecognitionRepository requests,
        IAuditService audit,
        IUnitOfWork unitOfWork,
        TimeProvider time,
        ILogger<FaceRecognitionService> logger)
    {
        _logger = logger;
        _currentUser = currentUser;
        _request = request;
        _settings = settings;
        _metering = metering;
        _images = images;
        _engine = engine;
        _index = index;
        _codec = codec;
        _encryption = encryption;
        _faces = faces;
        _requests = requests;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _time = time;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ---------------------------------------------------------------- Enroll

    public async Task<Result<EnrollFaceResponse>> EnrollAsync(EnrollFaceRequest request, byte[] image, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (Begin(MeteredOperation.Enroll, image, idempotencyKey, string.Join('\u001f', request.ExternalRef.Trim(), request.ConsentReference.Trim(), request.DisplayName?.Trim(), request.Metadata)) is not { } begin)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Face recognition is only available to client accounts.");
        }

        var clientId = begin.ClientId;
        if (await ReplayAsync(begin, cancellationToken) is { } stored)
        {
            return stored.Outcome != RecognitionOutcome.Enrolled || stored.Matches.Count == 0
                ? FailureFor(stored.Outcome)
                : new EnrollFaceResponse(stored.Matches[0].ProfileId, stored.Matches[0].TemplateId, false, stored.Matches[0].Score,
                    nameof(RecognitionOutcome.Enrolled), stored.Id, new CreditsDto(stored.CreditsCharged, stored.BalanceAfter));
        }

        var settings = await _settings.GetEffectiveAsync(clientId, cancellationToken);
        var gate = await _metering.PreflightAsync(clientId, MeteredOperation.Enroll, cancellationToken);
        if (gate.IsFailure)
        {
            return gate.Error!;
        }

        var existing = await _faces.GetProfileByExternalRefAsync(request.ExternalRef.Trim(), cancellationToken);
        if (existing is { Status: FaceProfileStatus.Disabled })
        {
            return Error.Conflict("PROFILE_DISABLED", "This person's profile is disabled.");
        }

        if (existing is not null && !string.Equals(existing.ConsentReference, request.ConsentReference.Trim(), StringComparison.Ordinal))
        {
            // Adding a face to an existing person changes who they are verified as: it needs the same consent the person gave.
            return Error.Conflict("CONSENT_MISMATCH", "The consent reference does not match the one recorded for this person.");
        }

        if (existing is null && await _faces.CountProfilesAsync(cancellationToken) >= settings.Int(SettingKeys.Limits.MaxProfiles))
        {
            return Error.Conflict("PROFILE_LIMIT_REACHED", "The maximum number of registered people for this account has been reached.");
        }

        if (await _faces.ImageHashExistsAsync(begin.RawSha256, cancellationToken))
        {
            return Error.Conflict("DUPLICATE_TEMPLATE", "This image is already registered.");
        }

        var prepared = await _images.PrepareAsync(image, cancellationToken);
        if (prepared.IsFailure)
        {
            return prepared.Error!;
        }

        var watch = Stopwatch.StartNew();
        var threshold = settings.Decimal(SettingKeys.Face.MatchThreshold);
        IReadOnlyList<DetectedFace> faces;
        try
        {
            faces = await _engine.DetectAsync(prepared.Value, cancellationToken);
        }
        catch (FaceProviderException ex)
        {
            return await ProviderFailureAsync(begin, threshold, watch, ex, cancellationToken);
        }

        var bad = Classify(faces, settings.Int(SettingKeys.Face.MaxFacesPerImage), settings.Decimal(SettingKeys.Face.MinQuality), enforceQuality: true);
        if (bad is not null)
        {
            return await FailAsync(begin, bad.Value, threshold, watch, null, cancellationToken);
        }

        var face = Largest(faces);
        float[] embedding;
        byte[] embeddingEnc;
        try
        {
            embedding = await _engine.ExtractAsync(prepared.Value, face, cancellationToken);
            embeddingEnc = await _codec.EncryptAsync(clientId, embedding, cancellationToken);
        }
        catch (FaceProviderException ex)
        {
            return await ProviderFailureAsync(begin, threshold, watch, ex, cancellationToken);
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? null
            : await _encryption.EncryptAsync(clientId, Encoding.UTF8.GetBytes(request.DisplayName.Trim()), "face-profile-name", cancellationToken);

        var now = Now;
        var profileCreated = existing is null;
        FaceProfile profile;
        try
        {
            profile = existing ?? FaceProfile.Create(clientId, request.ExternalRef, request.ConsentReference, now, now.AddDays(settings.Int(SettingKeys.Face.RetentionDays)));
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        var template = FaceTemplate.Create(clientId, profile.Id, _engine.Provider, _engine.ModelVersion, embedding.Length, embeddingEnc, face.Quality, begin.RawSha256, now);

        var outcome = await CommitAsync<EnrollFaceResponse>(
            begin, RecognitionOutcome.Enrolled, null, threshold, face.Quality, 1, profile.Id, watch, persistRequest: true,
            async (record, credits, ct) =>
            {
                if (profileCreated)
                {
                    profile.DisplayNameEnc = displayName;
                    profile.MetadataJson = string.IsNullOrWhiteSpace(request.Metadata) ? null : request.Metadata;
                    _faces.Add(profile);
                }
                else
                {
                    if (displayName is not null)
                    {
                        profile.DisplayNameEnc = displayName;
                    }

                    profile.RetentionUntil = now.AddDays(settings.Int(SettingKeys.Face.RetentionDays));
                    var current = await _faces.GetTemplatesAsync(profile.Id, ct);
                    foreach (var old in current.Take(Math.Max(0, current.Count - (FaceProfile.MaxTemplates - 1))))
                    {
                        _faces.Remove(old); // keep the newest templates; superseded biometrics are not retained
                    }
                }

                _faces.Add(template);
                record.AddMatch(profile.Id, template.Id, face.Quality, true);
                _audit.Record(new AuditEntry("face.enrolled", nameof(FaceProfile), profile.Id.ToString(), clientId, NewValues: new { profileCreated, record.CreditsCharged }));
            },
            (record, credits) => new EnrollFaceResponse(profile.Id, template.Id, profileCreated, face.Quality, nameof(RecognitionOutcome.Enrolled), record, credits),
            cancellationToken);

        if (outcome.IsSuccess)
        {
            _index.Invalidate(clientId);
        }

        return outcome;
    }

    // ---------------------------------------------------------------- Verify

    public async Task<Result<VerifyFaceResponse>> VerifyAsync(VerifyFaceRequest request, byte[] image, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (Begin(MeteredOperation.Verify, image, idempotencyKey, request.ProfileId?.ToString("N") ?? "ref:" + request.ExternalRef?.Trim()) is not { } begin)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Face recognition is only available to client accounts.");
        }

        if (await ReplayAsync(begin, cancellationToken) is { } stored)
        {
            if (stored.Outcome is not (RecognitionOutcome.Matched or RecognitionOutcome.NoMatch) || stored.Matches.Count == 0)
            {
                return FailureFor(stored.Outcome);
            }

            return new VerifyFaceResponse(stored.Outcome == RecognitionOutcome.Matched, stored.Outcome == RecognitionOutcome.Matched ? stored.Matches[0].Score : 0m, stored.ThresholdUsed,
                stored.Outcome.ToString(), stored.Matches[0].ProfileId, stored.Id, new CreditsDto(stored.CreditsCharged, stored.BalanceAfter));
        }

        var clientId = begin.ClientId;
        var settings = await _settings.GetEffectiveAsync(clientId, cancellationToken);
        var gate = await _metering.PreflightAsync(clientId, MeteredOperation.Verify, cancellationToken);
        if (gate.IsFailure)
        {
            return gate.Error!;
        }

        var profile = request.ProfileId is { } id
            ? await _faces.GetProfileAsync(id, cancellationToken)
            : await _faces.GetProfileByExternalRefAsync(request.ExternalRef!.Trim(), cancellationToken);
        if (profile is null)
        {
            return Error.NotFound("The person to verify against was not found.");
        }

        if (profile.Status != FaceProfileStatus.Active)
        {
            return Error.Conflict("PROFILE_DISABLED", "This person's profile is disabled.");
        }

        var stored1 = await _faces.GetActiveTemplatesAsync(profile.Id, _engine.Provider, _engine.ModelVersion, cancellationToken);
        if (stored1.Count == 0)
        {
            return Error.Conflict("PROFILE_HAS_NO_TEMPLATE", "This person has no registered face for the current recognition model.");
        }

        var prepared = await _images.PrepareAsync(image, cancellationToken);
        if (prepared.IsFailure)
        {
            return prepared.Error!;
        }

        var watch = Stopwatch.StartNew();
        var threshold = settings.Decimal(SettingKeys.Face.MatchThreshold);
        float[] probe;
        DetectedFace face;
        try
        {
            var faces = await _engine.DetectAsync(prepared.Value, cancellationToken);
            if (Classify(faces, settings.Int(SettingKeys.Face.MaxFacesPerImage), 0m, enforceQuality: false) is { } bad)
            {
                return await FailAsync(begin, bad, threshold, watch, profile.Id, cancellationToken);
            }

            face = Largest(faces);
            probe = await _engine.ExtractAsync(prepared.Value, face, cancellationToken);
        }
        catch (FaceProviderException ex)
        {
            return await ProviderFailureAsync(begin, threshold, watch, ex, cancellationToken);
        }

        var best = (Score: -1d, TemplateId: Guid.Empty);
        foreach (var t in stored1)
        {
            var score = _engine.Similarity(probe, await _codec.DecryptAsync(clientId, t.EmbeddingEnc, cancellationToken));
            if (score > best.Score)
            {
                best = (score, t.Id);
            }
        }

        var matched = (decimal)best.Score >= threshold;
        var outcome = matched ? RecognitionOutcome.Matched : RecognitionOutcome.NoMatch;
        return await CommitAsync<VerifyFaceResponse>(
            begin, outcome, null, threshold, (decimal)best.Score, 1, profile.Id, watch, persistRequest: true,
            (record, credits, ct) =>
            {
                record.AddMatch(profile.Id, best.TemplateId, (decimal)best.Score, matched);
                return Task.CompletedTask;
            },
            (record, credits) => new VerifyFaceResponse(matched, matched ? Math.Round((decimal)best.Score, 4) : 0m, threshold, outcome.ToString(), profile.Id, record, credits),
            cancellationToken);
    }

    // -------------------------------------------------------------- Identify

    public async Task<Result<IdentifyFaceResponse>> IdentifyAsync(IdentifyFaceRequest request, byte[] image, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (Begin(MeteredOperation.Identify, image, idempotencyKey, "k:" + request.TopK) is not { } begin)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Face recognition is only available to client accounts.");
        }

        if (await ReplayAsync(begin, cancellationToken) is { } stored)
        {
            if (stored.Outcome is not (RecognitionOutcome.Matched or RecognitionOutcome.NoMatch))
            {
                return FailureFor(stored.Outcome);
            }

            var refs = await _faces.GetExternalRefsAsync(stored.Matches.Where(m => m.IsMatch).Select(m => m.ProfileId).ToList(), cancellationToken);
            var replayed = stored.Matches.Where(m => m.IsMatch && refs.ContainsKey(m.ProfileId))
                .Select(m => new FaceMatchDto(m.ProfileId, refs[m.ProfileId], m.Score)).ToList();
            return new IdentifyFaceResponse(replayed, stored.Outcome == RecognitionOutcome.Matched ? stored.BestScore : null, stored.ThresholdUsed, stored.Outcome.ToString(), stored.Id,
                new CreditsDto(stored.CreditsCharged, stored.BalanceAfter));
        }

        var clientId = begin.ClientId;
        var settings = await _settings.GetEffectiveAsync(clientId, cancellationToken);
        var gate = await _metering.PreflightAsync(clientId, MeteredOperation.Identify, cancellationToken);
        if (gate.IsFailure)
        {
            return gate.Error!;
        }

        var prepared = await _images.PrepareAsync(image, cancellationToken);
        if (prepared.IsFailure)
        {
            return prepared.Error!;
        }

        var watch = Stopwatch.StartNew();
        var threshold = settings.Decimal(SettingKeys.Face.MatchThreshold);
        var topK = request.TopK ?? settings.Int(SettingKeys.Face.IdentifyTopK);
        float[] probe;
        try
        {
            var faces = await _engine.DetectAsync(prepared.Value, cancellationToken);
            if (Classify(faces, settings.Int(SettingKeys.Face.MaxFacesPerImage), 0m, enforceQuality: false) is { } bad)
            {
                return await FailAsync(begin, bad, threshold, watch, null, cancellationToken);
            }

            probe = await _engine.ExtractAsync(prepared.Value, Largest(faces), cancellationToken);
        }
        catch (FaceProviderException ex)
        {
            return await ProviderFailureAsync(begin, threshold, watch, ex, cancellationToken);
        }

        var candidates = await _index.GetAsync(clientId, _engine.Provider, _engine.ModelVersion, cancellationToken);
        var ranked = Scoring.Rank(_engine, probe, candidates).Take(topK).ToList();

        // A template erased on another node may still be in this node's cache: confirm against the database before answering.
        var alive = await _faces.ExistingTemplateIdsAsync(ranked.Select(r => r.Template.TemplateId).ToList(), cancellationToken);
        ranked = ranked.Where(r => alive.Contains(r.Template.TemplateId)).ToList();

        var best = ranked.Count == 0 ? (decimal?)null : (decimal)ranked[0].Score;
        var matches = ranked.Where(r => (decimal)r.Score >= threshold).ToList();
        var outcome = matches.Count > 0 ? RecognitionOutcome.Matched : RecognitionOutcome.NoMatch;
        var refsNow = await _faces.GetExternalRefsAsync(matches.Select(m => m.Template.ProfileId).ToList(), cancellationToken);

        return await CommitAsync<IdentifyFaceResponse>(
            begin, outcome, null, threshold, best, ranked.Count, null, watch, persistRequest: true,
            (record, credits, ct) =>
            {
                foreach (var r in ranked)
                {
                    record.AddMatch(r.Template.ProfileId, r.Template.TemplateId, (decimal)r.Score, (decimal)r.Score >= threshold);
                }

                return Task.CompletedTask;
            },
            (record, credits) => new IdentifyFaceResponse(
                matches.Where(m => refsNow.ContainsKey(m.Template.ProfileId))
                    .Select(m => new FaceMatchDto(m.Template.ProfileId, refsNow[m.Template.ProfileId], Math.Round((decimal)m.Score, 4))).ToList(),
                outcome == RecognitionOutcome.Matched && best is not null ? Math.Round(best.Value, 4) : null, threshold, outcome.ToString(), record, credits),
            cancellationToken);
    }

    // ---------------------------------------------------------------- Detect

    public async Task<Result<DetectFaceResponse>> DetectAsync(byte[] image, CancellationToken cancellationToken)
    {
        if (Begin(MeteredOperation.Detect, image, null) is not { } begin)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Face recognition is only available to client accounts.");
        }

        var gate = await _metering.PreflightAsync(begin.ClientId, MeteredOperation.Detect, cancellationToken);
        if (gate.IsFailure)
        {
            return gate.Error!;
        }

        var prepared = await _images.PrepareAsync(image, cancellationToken);
        if (prepared.IsFailure)
        {
            return prepared.Error!;
        }

        IReadOnlyList<DetectedFace> faces;
        try
        {
            faces = await _engine.DetectAsync(prepared.Value, cancellationToken);
        }
        catch (FaceProviderException)
        {
            return Error.Unavailable(ErrorCodes.FaceProviderUnavailable, "The face recognition service is temporarily unavailable.");
        }

        // Nothing is persisted for a detection; only the (normally free) charge is recorded in the ledger.
        var charge = await _metering.ChargeAsync(
            new ChargeCommand(begin.ClientId, MeteredOperation.Detect, MeterOutcome.Success, begin.RequestId, null), cancellationToken);
        if (charge.IsFailure)
        {
            return charge.Error!;
        }

        return new DetectFaceResponse(
            faces.Count,
            faces.Select(f => new DetectedFaceDto(f.X, f.Y, f.Width, f.Height, Math.Round(f.Quality, 4))).ToList(),
            begin.RequestId,
            new CreditsDto(charge.Value.Charged, charge.Value.RemainingBalance));
    }

    // ------------------------------------------------------------- plumbing

    private sealed record BeginContext(Guid ClientId, MeteredOperation Operation, byte[] RawSha256, string? DerivedKey, Guid RequestId);

    private BeginContext? Begin(MeteredOperation operation, byte[] image, string? idempotencyKey, string fingerprint = "")
    {
        if (_currentUser.ClientId is not { } clientId)
        {
            return null;
        }

        var sha = SHA256.HashData(image);
        string? derived = null;
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            // Bound to the operation and the exact image: reusing a key for anything else yields a different derived key.
            derived = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{clientId:N}|{operation}|{idempotencyKey.Trim()}|{Convert.ToHexString(sha)}|{fingerprint}")));
        }

        return new BeginContext(clientId, operation, sha, derived, Guid.CreateVersion7());
    }

    private async Task<RecognitionRequest?> ReplayAsync(BeginContext begin, CancellationToken cancellationToken) =>
        begin.DerivedKey is null ? null : await _requests.FindByIdempotencyKeyAsync(begin.DerivedKey, cancellationToken);

    /// <summary>Returns the outcome to reject the image with, or null when exactly one usable face is present.</summary>
    private static RecognitionOutcome? Classify(IReadOnlyList<DetectedFace> faces, int maxFaces, decimal minQuality, bool enforceQuality)
    {
        if (faces.Count == 0)
        {
            return RecognitionOutcome.NoFaceDetected;
        }

        if (faces.Count > Math.Max(1, maxFaces) || (enforceQuality && faces.Count > 1))
        {
            return RecognitionOutcome.MultipleFaces;
        }

        return enforceQuality && Largest(faces).Quality < minQuality ? RecognitionOutcome.LowQuality : null;
    }

    private static DetectedFace Largest(IReadOnlyList<DetectedFace> faces) => faces.OrderByDescending(f => f.Width * f.Height).First();

    private static Error FailureFor(RecognitionOutcome outcome) => outcome switch
    {
        RecognitionOutcome.NoFaceDetected => Error.Unprocessable(ErrorCodes.NoFaceDetected, "No face was detected in the image."),
        RecognitionOutcome.MultipleFaces => Error.Unprocessable(ErrorCodes.MultipleFaces, "More faces were found than allowed. Submit an image with a single face."),
        RecognitionOutcome.LowQuality => Error.Unprocessable(ErrorCodes.LowQualityImage, "The image quality is too low. Use a sharper, well-lit, front-facing photo."),
        _ => Error.Unavailable(ErrorCodes.FaceProviderUnavailable, "The face recognition service is temporarily unavailable."),
    };

    /// <summary>An image the engine could not use: recorded (and charged only if the licence's charge policy says so), then reported as 422.</summary>
    private async Task<Error> FailAsync(BeginContext begin, RecognitionOutcome outcome, decimal threshold, Stopwatch watch, Guid? target, CancellationToken cancellationToken)
    {
        var code = FailureFor(outcome).Code;
        var committed = await CommitAsync<bool>(
            begin, outcome, code, threshold, null, 0, target, watch, persistRequest: true,
            (_, _, _) => Task.CompletedTask, (_, _) => true, cancellationToken);
        return committed.IsFailure ? committed.Error! : FailureFor(outcome);
    }

    /// <summary>The provider failed: recorded for diagnostics, never billed, never replayed (a retry must reach the engine again).</summary>
    private async Task<Error> ProviderFailureAsync(BeginContext begin, decimal threshold, Stopwatch watch, FaceProviderException ex, CancellationToken cancellationToken)
    {
        _logger.LogError(ex, "Face provider {Provider} failed during {Operation}", _engine.Provider, begin.Operation);
        var recorded = RecognitionRequest.Create(
            begin.RequestId, begin.ClientId, begin.Operation, Source(), _currentUser.ActorId, null, RecognitionOutcome.ProviderError,
            ErrorCodes.FaceProviderUnavailable, threshold, null, 0, _engine.Provider, _engine.ModelVersion, begin.RawSha256,
            (int)watch.ElapsedMilliseconds, null, _request.IpAddress, _request.CorrelationId, Now);
        _requests.Add(recorded);
        await _unitOfWork.SaveChangesAsync(CancellationToken.None);
        return Error.Unavailable(ErrorCodes.FaceProviderUnavailable, "The face recognition service is temporarily unavailable.");
    }

    private RecognitionSource Source() => _currentUser.ActorType == ActorType.ApiKey ? RecognitionSource.Api : RecognitionSource.Portal;

    /// <summary>
    /// Charges the licence and records the attempt in ONE transaction. If the charge is refused (e.g. the last credits were taken by
    /// a parallel request) nothing is written and the error is returned, so a result is never delivered for free.
    /// </summary>
    private async Task<Result<T>> CommitAsync<T>(
        BeginContext begin,
        RecognitionOutcome outcome,
        string? errorCode,
        decimal threshold,
        decimal? bestScore,
        int candidates,
        Guid? target,
        Stopwatch watch,
        bool persistRequest,
        Func<RecognitionRequest, CreditsDto, CancellationToken, Task> persistExtras,
        Func<Guid, CreditsDto, T> build,
        CancellationToken cancellationToken)
    {
        var meter = outcome switch
        {
            RecognitionOutcome.Enrolled or RecognitionOutcome.Matched => MeterOutcome.Success,
            RecognitionOutcome.NoMatch => MeterOutcome.NoMatch,
            _ => MeterOutcome.Failed,
        };

        try
        {
            return await _unitOfWork.ExecuteInTransactionAsync<Result<T>>(
                async ct =>
                {
                    // No idempotency key is handed to the ledger: replay protection lives on the request row (unique index), which
                    // shares this transaction, so a losing racer's deduction is rolled back together with its failed insert.
                    var charge = await _metering.ChargeAsync(new ChargeCommand(begin.ClientId, begin.Operation, meter, begin.RequestId, null), ct);
                    if (charge.IsFailure)
                    {
                        return charge.Error!;
                    }

                    var credits = new CreditsDto(charge.Value.Charged, charge.Value.RemainingBalance);
                    var record = RecognitionRequest.Create(
                        begin.RequestId, begin.ClientId, begin.Operation, Source(), _currentUser.ActorId, target, outcome, errorCode, threshold,
                        bestScore, candidates, _engine.Provider, _engine.ModelVersion, begin.RawSha256, (int)watch.ElapsedMilliseconds,
                        begin.DerivedKey, _request.IpAddress, _request.CorrelationId, Now);
                    record.RecordCharge(charge.Value.Charged, charge.Value.RemainingBalance, charge.Value.TransactionId);
                    _requests.Add(record);
                    await persistExtras(record, credits, ct);
                    await _unitOfWork.SaveChangesAsync(ct);
                    return build(record.Id, credits);
                },
                cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            _unitOfWork.ClearTracked();
            return Error.Conflict(ErrorCodes.ConcurrencyConflict, "This person was changed by another request at the same time. Please retry.");
        }
        catch (UniqueConstraintViolationException)
        {
            _unitOfWork.ClearTracked();
            if (begin.DerivedKey is not null && await _requests.FindByIdempotencyKeyAsync(begin.DerivedKey, cancellationToken) is not null)
            {
                return Error.Conflict(ErrorCodes.Conflict, "An identical request is already being processed. Retry to receive its result.");
            }

            return begin.Operation == MeteredOperation.Enroll
                ? Error.Conflict(ErrorCodes.DuplicateExternalRef, "A person with this reference was registered at the same time.")
                : Error.Conflict(ErrorCodes.Conflict, "The request conflicted with a concurrent change. Please retry.");
        }
    }
}
