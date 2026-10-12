using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Common;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Modules;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Infrastructure.Persistence.Repositories;

internal sealed class VerificationRepository : IVerificationRepository
{
    private const int MaxPage = 500;
    private readonly AppDbContext _db;

    public VerificationRepository(AppDbContext db)
    {
        _db = db;
    }

    public void Add(VerificationRequest request) => _db.VerificationRequests.Add(request);

    public Task<VerificationRequest?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _db.VerificationRequests.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<VerificationRequest?> GetReadOnlyAsync(Guid id, CancellationToken cancellationToken) =>
        _db.VerificationRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<VerificationRequest?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        _db.VerificationRequests.AsNoTracking().FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<VerificationCreateOutcome> CreateIdempotentAsync(
        VerificationRequest request, VerificationDocument? document, VerificationDocumentBlob? blob, CancellationToken cancellationToken,
        VerificationResult? submission = null)
    {
        var key = request.IdempotencyKey;
        if (key is not null && await FindByIdempotencyKeyAsync(key, cancellationToken) is { } existing)
        {
            return new VerificationCreateOutcome(existing, false);
        }

        try
        {
            await _db.ExecuteInTransactionAsync(
                async token =>
                {
                    _db.VerificationRequests.Add(request);
                    if (document is not null && blob is not null)
                    {
                        _db.VerificationDocuments.Add(document);
                        _db.VerificationDocumentBlobs.Add(blob);
                    }

                    if (submission is not null)
                    {
                        // The encrypted submitted fields travel with the request so a request can never exist without its inputs.
                        _db.VerificationResults.Add(submission);
                    }

                    await _db.SaveChangesAsync(token);
                    return true;
                },
                cancellationToken);
            return new VerificationCreateOutcome(request, true);
        }
        catch (UniqueConstraintViolationException) when (key is not null)
        {
            _db.ChangeTracker.Clear();
            var winner = await FindByIdempotencyKeyAsync(key, cancellationToken);
            return winner is null ? throw new InvalidOperationException("Unique violation without an existing idempotent request.") : new VerificationCreateOutcome(winner, false);
        }
    }

    public async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (ConcurrencyConflictException)
        {
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    public Task<int> CountOpenAsync(CancellationToken cancellationToken) =>
        _db.VerificationRequests.CountAsync(r => r.Status == VerificationStatus.Queued || r.Status == VerificationStatus.Processing, cancellationToken);

    public Task<Guid?> FindLatestByDocumentHashAsync(byte[] sha256, CancellationToken cancellationToken) =>
        _db.VerificationRequests.AsNoTracking().Where(r => r.DocumentSha256 == sha256)
            .OrderByDescending(r => r.CreatedAt).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(cancellationToken);

    public async Task<(IReadOnlyList<VerificationListRow> Items, int Total)> ListAsync(
        VerificationListFilter filter, int skip, int take, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, MaxPage);
        skip = Math.Max(0, skip);
        var query = _db.VerificationRequests.AsNoTracking().AsQueryable();
        if (filter.Type is { } type)
        {
            query = query.Where(r => r.Type == type);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(r => r.Status == status);
        }

        if (filter.ReviewRequired is { } review)
        {
            query = query.Where(r => r.ReviewRequired == review);
        }

        if (filter.ExternalState is { } external)
        {
            query = query.Where(r => r.ExternalState == external);
        }

        if (filter.From is { } from)
        {
            query = query.Where(r => r.CreatedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(r => r.CreatedAt <= to);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            if (Guid.TryParse(search, out var id))
            {
                query = query.Where(r => r.Id == id);
            }
            else
            {
                var like = Escape(search) + "%";
                query = query.Where(r => EF.Functions.Like(r.Id.ToString(), like, "\\") || (r.ExternalRef != null && EF.Functions.Like(r.ExternalRef, like, "\\")));
            }
        }

        var total = await query.CountAsync(cancellationToken);
        var ordered = filter.Sort switch
        {
            VerificationListSort.CreatedAsc => query.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id),
            VerificationListSort.CompletedDesc => query.OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.CreatedAt).ThenBy(r => r.Id),
            _ => query.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id),
        };

        var rows = await ordered.Skip(skip).Take(take)
            .Select(r => new
            {
                r.Id, r.Type, r.Status, r.ReviewRequired, r.ExternalState, r.Assurance, r.ExternalRef, r.CreatedAt, r.CompletedAt, r.CreditsCharged, r.ProviderCreditsCharged,
                Masked = _db.VerificationResults.Where(x => x.RequestId == r.Id).Select(x => x.IdentifierMasked).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        return (rows.Select(r => new VerificationListRow(
            r.Id, r.Type, r.Status, r.ReviewRequired, r.ExternalState, r.Assurance, r.Masked, r.ExternalRef, r.CreatedAt, r.CompletedAt, r.CreditsCharged, r.ProviderCreditsCharged)).ToList(), total);
    }

    public async Task<IReadOnlyList<VerificationEvent>> ListEventsAsync(Guid requestId, int take, CancellationToken cancellationToken) =>
        await _db.VerificationEvents.AsNoTracking().Where(e => e.RequestId == requestId).OrderBy(e => e.Id).Take(Math.Clamp(take, 1, MaxPage)).ToListAsync(cancellationToken);

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}

internal sealed class VerificationDocumentStore : IVerificationDocumentStore
{
    private readonly AppDbContext _db;

    public VerificationDocumentStore(AppDbContext db)
    {
        _db = db;
    }

    public void Add(VerificationDocument document, VerificationDocumentBlob blob)
    {
        _db.VerificationDocuments.Add(document);
        _db.VerificationDocumentBlobs.Add(blob);
    }

    public Task<VerificationDocument?> GetAsync(Guid requestId, CancellationToken cancellationToken) =>
        _db.VerificationDocuments.FirstOrDefaultAsync(d => d.RequestId == requestId, cancellationToken);

    public Task<DocumentCiphertext?> GetCiphertextAsync(Guid requestId, CancellationToken cancellationToken) =>
        (from d in _db.VerificationDocuments.AsNoTracking()
         where d.RequestId == requestId && d.PurgedAt == null
         join b in _db.VerificationDocumentBlobs.AsNoTracking() on d.Id equals b.DocumentId
         select new DocumentCiphertext(d.Id, d.RequestId, b.Content)).FirstOrDefaultAsync(cancellationToken);
}

internal sealed class VerificationModuleSettingStore : IVerificationModuleSettings, IVerificationModuleSettingReader
{
    private readonly AppDbContext _db;

    public VerificationModuleSettingStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<VerificationModuleSetting>> ListAsync(CancellationToken cancellationToken) =>
        await _db.VerificationModuleSettings.OrderBy(s => s.ModuleKey).ToListAsync(cancellationToken);

    public Task<VerificationModuleSetting?> GetAsync(string moduleKey, CancellationToken cancellationToken) =>
        _db.VerificationModuleSettings.FirstOrDefaultAsync(s => s.ModuleKey == moduleKey, cancellationToken);

    public void Add(VerificationModuleSetting setting) => _db.VerificationModuleSettings.Add(setting);

    public void SetExpectedVersion(VerificationModuleSetting setting, byte[] rowVersion) =>
        _db.Entry(setting).Property(s => s.RowVersion).OriginalValue = rowVersion;

    public Task<bool?> IsEnabledAsync(string moduleKey, CancellationToken cancellationToken) =>
        _db.VerificationModuleSettings.AsNoTracking().Where(s => s.ModuleKey == moduleKey).Select(s => (bool?)s.IsEnabled).FirstOrDefaultAsync(cancellationToken);
}
