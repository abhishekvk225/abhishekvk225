using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Faces;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Infrastructure.Persistence.Repositories;

internal sealed class FaceRepository : IFaceRepository
{
    private readonly AppDbContext _db;

    public FaceRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<FaceProfile?> GetProfileAsync(Guid id, CancellationToken cancellationToken) =>
        _db.FaceProfiles.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<FaceProfile?> GetProfileByExternalRefAsync(string externalRef, CancellationToken cancellationToken) =>
        _db.FaceProfiles.FirstOrDefaultAsync(p => p.ExternalRef == externalRef, cancellationToken);

    public Task<int> CountProfilesAsync(CancellationToken cancellationToken) => _db.FaceProfiles.CountAsync(cancellationToken);

    public async Task<(IReadOnlyList<FaceProfileListRow> Items, int Total)> ListProfilesAsync(
        string? search, FaceProfileStatus? status, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _db.FaceProfiles.AsNoTracking().AsQueryable();
        if (status is { } s)
        {
            query = query.Where(p => p.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%";
            query = query.Where(p => EF.Functions.Like(p.ExternalRef, like, "\\"));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id).Skip(skip).Take(take)
            .Select(p => new { Profile = p, Count = _db.FaceTemplates.Count(t => t.ProfileId == p.Id) })
            .ToListAsync(cancellationToken);
        return (rows.Select(r => new FaceProfileListRow(r.Profile, r.Count)).ToList(), total);
    }

    public async Task<IReadOnlyList<FaceTemplate>> GetTemplatesAsync(Guid profileId, CancellationToken cancellationToken) =>
        await _db.FaceTemplates.Where(t => t.ProfileId == profileId).OrderBy(t => t.CreatedAt).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<FaceTemplate>> GetActiveTemplatesAsync(Guid profileId, string provider, string modelVersion, CancellationToken cancellationToken) =>
        await _db.FaceTemplates.AsNoTracking()
            .Where(t => t.ProfileId == profileId && t.Provider == provider && t.ModelVersion == modelVersion && t.Status == TemplateStatus.Active)
            .ToListAsync(cancellationToken);

    public Task<bool> ImageHashExistsAsync(byte[] sha256, CancellationToken cancellationToken) =>
        _db.FaceTemplates.AnyAsync(t => t.ImageSha256 == sha256, cancellationToken);

    public async Task<HashSet<Guid>> ExistingTemplateIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? []
            : (await _db.FaceTemplates.AsNoTracking()
                .Where(t => ids.Contains(t.Id) && t.Status == TemplateStatus.Active && _db.FaceProfiles.Any(p => p.Id == t.ProfileId && p.Status == FaceProfileStatus.Active))
                .Select(t => t.Id).ToListAsync(cancellationToken)).ToHashSet();

    public async Task<Dictionary<Guid, string>> GetExternalRefsAsync(IReadOnlyCollection<Guid> profileIds, CancellationToken cancellationToken) =>
        profileIds.Count == 0
            ? []
            : await _db.FaceProfiles.AsNoTracking().Where(p => profileIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ExternalRef, cancellationToken);

    public async Task<IReadOnlyList<Guid>> GetDueForRetentionAsync(DateTime now, int take, CancellationToken cancellationToken) =>
        await _db.FaceProfiles.AsNoTracking().Where(p => p.RetentionUntil != null && p.RetentionUntil <= now)
            .OrderBy(p => p.RetentionUntil).Take(take).Select(p => p.Id).ToListAsync(cancellationToken);

    public void Add(FaceProfile profile) => _db.FaceProfiles.Add(profile);

    public void Add(FaceTemplate template) => _db.FaceTemplates.Add(template);

    public void Remove(FaceProfile profile) => _db.FaceProfiles.Remove(profile);

    public void Remove(FaceTemplate template) => _db.FaceTemplates.Remove(template);
}

internal sealed class RecognitionRepository : IRecognitionRepository
{
    private readonly AppDbContext _db;

    public RecognitionRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<RecognitionRequest?> FindByIdempotencyKeyAsync(string key, CancellationToken cancellationToken) =>
        _db.RecognitionRequests.AsNoTracking().Include(r => r.Matches).FirstOrDefaultAsync(r => r.IdempotencyKey == key, cancellationToken);

    public Task<RecognitionRequest?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _db.RecognitionRequests.AsNoTracking().Include(r => r.Matches).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<RecognitionRequest> Items, int Total)> ListAsync(
        MeteredOperation? operation, RecognitionOutcome? outcome, Guid? profileId, DateTime? from, DateTime? to, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _db.RecognitionRequests.AsNoTracking().AsQueryable();
        if (operation is { } op)
        {
            query = query.Where(r => r.Operation == op);
        }

        if (outcome is { } oc)
        {
            query = query.Where(r => r.Outcome == oc);
        }

        if (profileId is { } pid)
        {
            query = query.Where(r => r.TargetProfileId == pid || r.Matches.Any(m => m.ProfileId == pid));
        }

        if (from is { } f)
        {
            query = query.Where(r => r.CreatedAt >= f);
        }

        if (to is { } t)
        {
            query = query.Where(r => r.CreatedAt < t);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }

    public void Add(RecognitionRequest request) => _db.RecognitionRequests.Add(request);
}
