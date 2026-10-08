using System.Text;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Faces;
using NexaVerify.Domain.Faces;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.Faces;

public interface IFaceProfileService
{
    Task<Result<PagedResult<FaceProfileListItemDto>>> ListAsync(FaceProfileListQuery query, CancellationToken cancellationToken);

    Task<Result<FaceProfileDto>> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<FaceProfileDto>> UpdateAsync(Guid id, UpdateFaceProfileRequest request, CancellationToken cancellationToken);

    /// <summary>Irreversibly erases the person and every template.</summary>
    Task<Result> EraseAsync(Guid id, CancellationToken cancellationToken);

    Task<Result> DeleteTemplateAsync(Guid profileId, Guid templateId, CancellationToken cancellationToken);

    Task<Result<FaceBalanceDto>> GetBalanceAsync(CancellationToken cancellationToken);
}

public sealed class FaceProfileService : IFaceProfileService
{
    private const string NamePurpose = "face-profile-name";

    private readonly IFaceRepository _faces;
    private readonly IClientEncryption _encryption;
    private readonly ICurrentUser _currentUser;
    private readonly IMeteringStore _metering;
    private readonly ITemplateIndex _index;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;

    public FaceProfileService(
        IFaceRepository faces, IClientEncryption encryption, ICurrentUser currentUser, IMeteringStore metering, ITemplateIndex index,
        IAuditService audit, IUnitOfWork unitOfWork, TimeProvider time)
    {
        _faces = faces;
        _encryption = encryption;
        _currentUser = currentUser;
        _metering = metering;
        _index = index;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _time = time;
    }

    public async Task<Result<PagedResult<FaceProfileListItemDto>>> ListAsync(FaceProfileListQuery query, CancellationToken cancellationToken)
    {
        if (ClientId is not { } clientId)
        {
            return Denied;
        }

        var paging = new PageRequest { Page = query.Page, PageSize = query.PageSize }.Normalize();
        FaceProfileStatus? status = Enum.TryParse<FaceProfileStatus>(query.Status, true, out var parsed) ? parsed : null;
        var (rows, total) = await _faces.ListProfilesAsync(query.Search?.Trim(), status, paging.Skip, paging.PageSize, cancellationToken);
        var items = new List<FaceProfileListItemDto>(rows.Count);
        foreach (var row in rows)
        {
            var p = row.Profile;
            items.Add(new FaceProfileListItemDto(p.Id, p.ExternalRef, await NameAsync(clientId, p, cancellationToken), p.Status.ToString(), row.TemplateCount, p.RetentionUntil, p.CreatedAt));
        }

        return new PagedResult<FaceProfileListItemDto>(items, paging.Page, paging.PageSize, total);
    }

    public async Task<Result<FaceProfileDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (ClientId is not { } clientId)
        {
            return Denied;
        }

        var profile = await _faces.GetProfileAsync(id, cancellationToken);
        return profile is null ? Error.NotFound() : await ToDtoAsync(clientId, profile, cancellationToken);
    }

    public async Task<Result<FaceProfileDto>> UpdateAsync(Guid id, UpdateFaceProfileRequest request, CancellationToken cancellationToken)
    {
        if (ClientId is not { } clientId)
        {
            return Denied;
        }

        var profile = await _faces.GetProfileAsync(id, cancellationToken);
        if (profile is null)
        {
            return Error.NotFound();
        }

        if (request.DisplayName is not null)
        {
            profile.DisplayNameEnc = request.DisplayName.Trim().Length == 0
                ? null
                : await _encryption.EncryptAsync(clientId, Encoding.UTF8.GetBytes(request.DisplayName.Trim()), NamePurpose, cancellationToken);
        }

        if (request.Metadata is not null)
        {
            profile.MetadataJson = string.IsNullOrWhiteSpace(request.Metadata) ? null : request.Metadata;
        }

        if (request.RetentionUntil is { } until)
        {
            profile.RetentionUntil = DateTime.SpecifyKind(until, DateTimeKind.Utc);
        }

        if (request.Status is { } status)
        {
            if (status == "Disabled")
            {
                profile.Disable();
            }
            else
            {
                profile.Enable();
            }
        }

        _audit.Record(new AuditEntry("face.profile_updated", nameof(FaceProfile), profile.Id.ToString(), clientId, NewValues: new { Status = profile.Status.ToString(), request.RetentionUntil }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _index.Invalidate(clientId); // a disabled person must stop matching immediately
        return await ToDtoAsync(clientId, profile, cancellationToken);
    }

    public async Task<Result> EraseAsync(Guid id, CancellationToken cancellationToken)
    {
        if (ClientId is not { } clientId)
        {
            return Denied;
        }

        var profile = await _faces.GetProfileAsync(id, cancellationToken);
        if (profile is null)
        {
            return Error.NotFound();
        }

        // Templates go with the profile (cascade). History keeps only the profile id, which identifies nobody once the profile is gone.
        _faces.Remove(profile);
        _audit.Record(new AuditEntry("face.profile_erased", nameof(FaceProfile), profile.Id.ToString(), clientId, OldValues: new { profile.ExternalRef }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _index.Invalidate(clientId);
        return Result.Success();
    }

    public async Task<Result> DeleteTemplateAsync(Guid profileId, Guid templateId, CancellationToken cancellationToken)
    {
        if (ClientId is not { } clientId)
        {
            return Denied;
        }

        var templates = await _faces.GetTemplatesAsync(profileId, cancellationToken);
        var template = templates.FirstOrDefault(t => t.Id == templateId);
        if (template is null)
        {
            return Error.NotFound();
        }

        _faces.Remove(template);
        _audit.Record(new AuditEntry("face.template_erased", nameof(FaceTemplate), templateId.ToString(), clientId, OldValues: new { ProfileId = profileId }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _index.Invalidate(clientId);
        return Result.Success();
    }

    public async Task<Result<FaceBalanceDto>> GetBalanceAsync(CancellationToken cancellationToken)
    {
        if (ClientId is not { } clientId)
        {
            return Denied;
        }

        var usable = await _metering.GetUsableAsync(clientId, _time.GetUtcNow().UtcDateTime, cancellationToken);
        return usable.Count == 0
            ? new FaceBalanceDto(0, null, "Unavailable")
            : new FaceBalanceDto(usable.Sum(l => l.Remaining), usable.Min(l => l.ExpiresAt), "Active");
    }

    private Guid? ClientId => _currentUser.ClientId;

    private static Error Denied => Error.Forbidden(Contracts.Common.ErrorCodes.Forbidden, "Face recognition is only available to client accounts.");

    private async Task<string?> NameAsync(Guid clientId, FaceProfile profile, CancellationToken cancellationToken) =>
        profile.DisplayNameEnc is null ? null : Encoding.UTF8.GetString(await _encryption.DecryptAsync(clientId, profile.DisplayNameEnc, NamePurpose, cancellationToken));

    private async Task<FaceProfileDto> ToDtoAsync(Guid clientId, FaceProfile p, CancellationToken cancellationToken)
    {
        var templates = (await _faces.GetTemplatesAsync(p.Id, cancellationToken))
            .Select(t => new FaceTemplateDto(t.Id, t.Provider, t.ModelVersion, t.QualityScore, t.Status.ToString(), t.CreatedAt)).ToList();
        return new FaceProfileDto(
            p.Id, p.ExternalRef, await NameAsync(clientId, p, cancellationToken), p.MetadataJson, p.Status.ToString(), p.ConsentReference,
            p.ConsentRecordedAt, p.RetentionUntil, templates.Count, templates, p.CreatedAt, p.UpdatedAt);
    }
}

public interface IRecognitionHistoryService
{
    Task<Result<PagedResult<RecognitionRequestDto>>> ListAsync(RecognitionHistoryQuery query, CancellationToken cancellationToken);

    Task<Result<RecognitionRequestDetailDto>> GetAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class RecognitionHistoryService : IRecognitionHistoryService
{
    private readonly IRecognitionRepository _requests;
    private readonly IFaceRepository _faces;

    public RecognitionHistoryService(IRecognitionRepository requests, IFaceRepository faces)
    {
        _requests = requests;
        _faces = faces;
    }

    public async Task<Result<PagedResult<RecognitionRequestDto>>> ListAsync(RecognitionHistoryQuery query, CancellationToken cancellationToken)
    {
        var paging = new PageRequest { Page = query.Page, PageSize = query.PageSize }.Normalize();
        MeteredOperation? operation = Enum.TryParse<MeteredOperation>(query.Operation, true, out var op) ? op : null;
        RecognitionOutcome? outcome = Enum.TryParse<RecognitionOutcome>(query.Outcome, true, out var oc) ? oc : null;
        var (items, total) = await _requests.ListAsync(operation, outcome, query.ProfileId, Utc(query.From), Utc(query.To), paging.Skip, paging.PageSize, cancellationToken);
        return new PagedResult<RecognitionRequestDto>(items.Select(ToDto).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<Result<RecognitionRequestDetailDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var request = await _requests.GetAsync(id, cancellationToken);
        if (request is null)
        {
            return Error.NotFound();
        }

        var refs = await _faces.GetExternalRefsAsync(request.Matches.Select(m => m.ProfileId).Distinct().ToList(), cancellationToken);
        var candidates = request.Matches.OrderBy(m => m.Rank)
            .Select(m => new RecognitionCandidateDto(m.Rank, m.ProfileId, refs.GetValueOrDefault(m.ProfileId), m.Score, m.IsMatch)).ToList();
        return new RecognitionRequestDetailDto(ToDto(request), request.BalanceAfter, candidates);
    }

    private static DateTime? Utc(DateTime? value) => value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

    private static RecognitionRequestDto ToDto(RecognitionRequest r) => new(
        r.Id, r.Operation.ToString(), r.Source.ToString(), r.Status.ToString(), r.Outcome.ToString(), r.ErrorCode, r.ThresholdUsed, r.BestScore,
        r.CandidateCount, r.Provider, r.LatencyMs, r.CreditsCharged, r.TargetProfileId, r.CreatedAt);
}

public interface IFaceRetentionProcessor
{
    /// <summary>Erases up to <paramref name="batchSize"/> profiles of the CURRENT tenant whose retention deadline has passed.</summary>
    Task<int> ProcessAsync(int batchSize, CancellationToken cancellationToken);
}

public sealed class FaceRetentionProcessor : IFaceRetentionProcessor
{
    private readonly IFaceRepository _faces;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITemplateIndex _index;
    private readonly TimeProvider _time;
    private readonly ITenantContext _tenant;

    public FaceRetentionProcessor(IFaceRepository faces, IAuditService audit, IUnitOfWork unitOfWork, ITemplateIndex index, TimeProvider time, ITenantContext tenant)
    {
        _faces = faces;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _index = index;
        _time = time;
        _tenant = tenant;
    }

    public async Task<int> ProcessAsync(int batchSize, CancellationToken cancellationToken)
    {
        var clientId = _tenant.ClientId ?? throw new InvalidOperationException("Retention runs inside a tenant scope.");
        var due = await _faces.GetDueForRetentionAsync(_time.GetUtcNow().UtcDateTime, batchSize, cancellationToken);
        foreach (var id in due)
        {
            if (await _faces.GetProfileAsync(id, cancellationToken) is not { } profile)
            {
                continue;
            }

            _faces.Remove(profile);
            _audit.Record(new AuditEntry("face.profile_retention_erased", nameof(FaceProfile), id.ToString(), clientId, OldValues: new { profile.ExternalRef, profile.RetentionUntil }));
        }

        if (due.Count > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            _index.Invalidate(clientId);
        }

        return due.Count;
    }
}
