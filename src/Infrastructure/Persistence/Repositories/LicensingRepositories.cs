using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Infrastructure.Persistence.Repositories;

internal sealed class LicenseRepository : ILicenseRepository
{
    private readonly AppDbContext _db;

    public LicenseRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<License?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Licenses.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);

    public async Task<LicenseRow?> GetRowAsync(Guid id, CancellationToken cancellationToken)
    {
        var license = await _db.Licenses.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        return license is null ? null : (await Rows([license], cancellationToken))[0];
    }

    public Task<bool> KeyExistsAsync(string licenseKey, CancellationToken cancellationToken) =>
        _db.Licenses.AnyAsync(l => l.LicenseKey == licenseKey, cancellationToken);

    public async Task<(IReadOnlyList<LicenseRow> Items, int Total)> ListAsync(
        Guid? clientId, LicenseStatus? status, int? expiringInDays, string? search, DateTime now, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _db.Licenses.AsNoTracking().AsQueryable();
        if (clientId is { } cid)
        {
            query = query.Where(l => l.ClientId == cid);
        }

        if (status is { } s)
        {
            query = s == LicenseStatus.Expired
                ? query.Where(l => l.Status == LicenseStatus.Expired || (l.Status != LicenseStatus.Revoked && l.ExpiresAt <= now))
                : query.Where(l => l.Status == s && l.ExpiresAt > now);
        }

        if (expiringInDays is { } days)
        {
            var until = now.AddDays(days);
            query = query.Where(l => l.Status == LicenseStatus.Active && l.ExpiresAt > now && l.ExpiresAt <= until);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(l => l.Name.Contains(term) || l.LicenseKey.Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var page = await query.OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (await Rows(page, cancellationToken), total);
    }

    public async Task<IReadOnlyList<LicenseRow>> ListForClientAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var list = await _db.Licenses.AsNoTracking().Where(l => l.ClientId == clientId)
            .OrderBy(l => l.ExpiresAt).ThenBy(l => l.Id).ToListAsync(cancellationToken);
        return await Rows(list, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetDueForExpiryAsync(DateTime now, int max, CancellationToken cancellationToken) =>
        await _db.Licenses.AsNoTracking()
            .Where(l => l.ExpiresAt <= now && l.Status != LicenseStatus.Expired && l.Status != LicenseStatus.Revoked)
            .OrderBy(l => l.ExpiresAt).Select(l => l.Id).Take(max).ToListAsync(cancellationToken);

    public void Add(License license) => _db.Licenses.Add(license);

    public void SetExpectedVersion(License license, byte[] rowVersion) =>
        _db.Entry(license).Property(l => l.RowVersion).OriginalValue = rowVersion;

    private async Task<IReadOnlyList<LicenseRow>> Rows(IReadOnlyList<License> licenses, CancellationToken cancellationToken)
    {
        var clientIds = licenses.Select(l => l.ClientId).Distinct().ToList();
        var planIds = licenses.Where(l => l.PlanId != null).Select(l => l.PlanId!.Value).Distinct().ToList();
        var clients = await _db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var plans = await _db.Plans.AsNoTracking().Where(p => planIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        return licenses
            .Select(l => new LicenseRow(l, clients.GetValueOrDefault(l.ClientId, string.Empty), l.PlanId is { } pid ? plans.GetValueOrDefault(pid) : null))
            .ToList();
    }
}

internal sealed class LedgerRepository : ILedgerRepository
{
    private readonly AppDbContext _db;

    public LedgerRepository(AppDbContext db)
    {
        _db = db;
    }

    public void Add(LicenseTransaction entry) => _db.LicenseTransactions.Add(entry);

    public async Task<byte[]?> GetTailHashAsync(Guid licenseId, CancellationToken cancellationToken) =>
        await _db.LicenseTransactions.AsNoTracking().Where(t => t.LicenseId == licenseId)
            .OrderByDescending(t => t.Id).Select(t => t.RowHash).FirstOrDefaultAsync(cancellationToken);

    public Task<LicenseTransaction?> GetAsync(long id, CancellationToken cancellationToken) =>
        _db.LicenseTransactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public Task<LicenseTransaction?> FindByIdempotencyKeyAsync(Guid clientId, string key, CancellationToken cancellationToken) =>
        _db.LicenseTransactions.AsNoTracking().FirstOrDefaultAsync(t => t.ClientId == clientId && t.IdempotencyKey == key, cancellationToken);

    public Task<bool> HasRefundForAsync(long consumptionId, CancellationToken cancellationToken) =>
        _db.LicenseTransactions.AnyAsync(t => t.ReferenceTransactionId == consumptionId && t.Type == LedgerEntryType.Refund, cancellationToken);

    public async Task<(IReadOnlyList<LicenseTransaction> Items, int Total)> ListAsync(Guid licenseId, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _db.LicenseTransactions.AsNoTracking().Where(t => t.LicenseId == licenseId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(t => t.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<LicenseTransaction>> GetAllAsync(Guid licenseId, CancellationToken cancellationToken) =>
        await _db.LicenseTransactions.AsNoTracking().Where(t => t.LicenseId == licenseId).OrderBy(t => t.Id).ToListAsync(cancellationToken);
}

internal sealed class PlanRepository : IPlanRepository
{
    private readonly AppDbContext _db;

    public PlanRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Plan>> ListAsync(CancellationToken cancellationToken) =>
        await _db.Plans.OrderBy(p => p.Name).ToListAsync(cancellationToken);

    public Task<Plan?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Plans.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<bool> CodeExistsAsync(string normalizedCode, Guid? exceptId, CancellationToken cancellationToken) =>
        _db.Plans.AnyAsync(p => p.Code == normalizedCode && p.Id != exceptId, cancellationToken);

    public void Add(Plan plan) => _db.Plans.Add(plan);
}

internal sealed class CostRuleRepository : ICostRuleRepository
{
    private readonly AppDbContext _db;

    public CostRuleRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<CostRule>> ListPlatformRulesAsync(CancellationToken cancellationToken) =>
        await _db.CostRules.OrderBy(r => r.Operation).ThenByDescending(r => r.EffectiveFrom).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ClientCostRule>> ListClientRulesAsync(Guid clientId, CancellationToken cancellationToken) =>
        await _db.ClientCostRules.Where(r => r.ClientId == clientId).OrderBy(r => r.Operation).ThenByDescending(r => r.EffectiveFrom).ToListAsync(cancellationToken);

    public Task<ClientCostRule?> FindClientRuleAsync(Guid clientId, MeteredOperation operation, DateTime at, CancellationToken cancellationToken) =>
        _db.ClientCostRules.AsNoTracking()
            .Where(r => r.ClientId == clientId && r.Operation == operation && r.IsActive && r.EffectiveFrom <= at && (r.EffectiveTo == null || at < r.EffectiveTo))
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<CostRule>> FindPlatformRulesAsync(MeteredOperation operation, DateTime at, CancellationToken cancellationToken) =>
        await _db.CostRules.AsNoTracking()
            .Where(r => r.Operation == operation && r.IsActive && r.EffectiveFrom <= at && (r.EffectiveTo == null || at < r.EffectiveTo))
            .OrderByDescending(r => r.EffectiveFrom)
            .ToListAsync(cancellationToken);

    public void Add(CostRule rule) => _db.CostRules.Add(rule);

    public void Add(ClientCostRule rule) => _db.ClientCostRules.Add(rule);
}
