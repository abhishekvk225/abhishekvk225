using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Licensing;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Infrastructure.Persistence.Queries;

/// <summary>Read-only ledger access for the verifier. Keyset-paged, no tracking, served by the (LicenseId, Id) index.</summary>
internal sealed class LedgerVerificationStore : ILedgerVerificationStore
{
    private readonly AppDbContext _db;

    public LedgerVerificationStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<LicenseLedgerHead>> ListLicensesAsync(Guid? afterLicenseId, int take, CancellationToken cancellationToken)
    {
        var query = _db.Licenses.AsNoTracking().AsQueryable();
        if (afterLicenseId is { } after)
        {
            query = query.Where(l => l.Id.CompareTo(after) > 0);
        }

        return await query.OrderBy(l => l.Id).Take(take)
            .Select(l => new LicenseLedgerHead(l.Id, l.ClientId, l.TotalCredits - l.ConsumedCredits))
            .ToListAsync(cancellationToken);
    }

    public Task<LicenseLedgerHead?> GetHeadAsync(Guid licenseId, CancellationToken cancellationToken) =>
        _db.Licenses.AsNoTracking().Where(l => l.Id == licenseId)
            .Select(l => new LicenseLedgerHead(l.Id, l.ClientId, l.TotalCredits - l.ConsumedCredits))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<LicenseTransaction>> ListEntriesAsync(Guid licenseId, long afterEntryId, int take, CancellationToken cancellationToken) =>
        await _db.LicenseTransactions.AsNoTracking()
            .Where(t => t.LicenseId == licenseId && t.Id > afterEntryId)
            .OrderBy(t => t.Id).Take(take)
            .ToListAsync(cancellationToken);
}

internal sealed class LicenseAlertRepository : ILicenseAlertRepository
{
    private readonly AppDbContext _db;

    public LicenseAlertRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<bool> ExistsAsync(Guid subjectId, LicenseAlertType type, string bucket, CancellationToken cancellationToken) =>
        _db.LicenseAlerts.AsNoTracking().AnyAsync(a => a.SubjectId == subjectId && a.AlertType == type && a.Bucket == bucket, cancellationToken);

    public Task<LicenseAlert?> GetAsync(Guid id, CancellationToken cancellationToken) => _db.LicenseAlerts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<LicenseAlert> Items, int Total)> ListAsync(bool unreadOnly, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _db.LicenseAlerts.AsNoTracking().AsQueryable();
        if (unreadOnly)
        {
            query = query.Where(a => a.ReadAt == null);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<int> CountUnreadAsync(CancellationToken cancellationToken) => _db.LicenseAlerts.AsNoTracking().CountAsync(a => a.ReadAt == null, cancellationToken);

    public void Add(LicenseAlert alert) => _db.LicenseAlerts.Add(alert);
}

/// <summary>Platform-scope worklists: only licenses/keys that could be due for an alert right now, for active clients.</summary>
internal sealed class LicenseAlertCandidates : ILicenseAlertCandidates
{
    private readonly AppDbContext _db;

    public LicenseAlertCandidates(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<LicenseAlertCandidate>> ListLicensesAsync(
        DateTime now, LicenseAlertOptions options, Guid? afterLicenseId, int take, CancellationToken cancellationToken)
    {
        var percent = options.LowBalancePercent;
        var noticeEnd = now.AddDays(options.ExpiringThresholds().DefaultIfEmpty(0).Max());
        var lookbackStart = now.AddDays(-options.ExpiredLookbackDays);

        var query = from l in _db.Licenses.AsNoTracking()
                    join c in _db.Clients.AsNoTracking() on l.ClientId equals c.Id
                    where c.Status == ClientStatus.Active && !c.IsSystem
                          && ((l.Status == LicenseStatus.Active && l.StartsAt <= now && l.ExpiresAt > now && l.TotalCredits > 0
                               && (l.TotalCredits - l.ConsumedCredits) * 100L <= percent * (long)l.TotalCredits)
                              || (l.Status == LicenseStatus.Active && l.ExpiresAt > now && l.ExpiresAt <= noticeEnd)
                              || ((l.Status == LicenseStatus.Active || l.Status == LicenseStatus.Expired) && l.ExpiresAt <= now && l.ExpiresAt >= lookbackStart))
                    select l;
        if (afterLicenseId is { } after)
        {
            query = query.Where(l => l.Id.CompareTo(after) > 0);
        }

        return await query.OrderBy(l => l.Id).Take(take)
            .Select(l => new LicenseAlertCandidate(l.Id, l.ClientId, l.Name, l.Status, l.TotalCredits, l.ConsumedCredits, l.StartsAt, l.ExpiresAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ApiKeyAlertCandidate>> ListApiKeysAsync(
        DateTime now, LicenseAlertOptions options, Guid? afterKeyId, int take, CancellationToken cancellationToken)
    {
        var until = now.AddDays(options.ApiKeyExpiringDays);
        var query = from k in _db.ApiKeys.AsNoTracking()
                    join c in _db.Clients.AsNoTracking() on k.ClientId equals c.Id
                    where c.Status == ClientStatus.Active && !c.IsSystem
                          && k.Status == Domain.Api.ApiKeyStatus.Active && k.ExpiresAt != null && k.ExpiresAt > now && k.ExpiresAt <= until
                    select k;
        if (afterKeyId is { } after)
        {
            query = query.Where(k => k.Id.CompareTo(after) > 0);
        }

        return await query.OrderBy(k => k.Id).Take(take)
            .Select(k => new ApiKeyAlertCandidate(k.Id, k.ClientId, k.Name, k.KeyPrefix, k.ExpiresAt!.Value))
            .ToListAsync(cancellationToken);
    }
}
