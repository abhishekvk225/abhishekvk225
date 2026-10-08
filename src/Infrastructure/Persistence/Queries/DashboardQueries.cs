using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Dashboards;
using NexaVerify.Contracts.Dashboards;
using NexaVerify.Domain.Api;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Infrastructure.Persistence.Queries;

/// <summary>
/// Projection-only, no-tracking SQL aggregates (GROUP BY in the database; only the grouped rows cross the wire). Tenant isolation is
/// the context's normal query filter + row-level security: no filter is bypassed here, so a tenant sees only its rows and platform
/// scope sees non-strict rows of every tenant (face tables stay invisible to it).
/// </summary>
internal sealed class DashboardQueries : IDashboardQueries
{
    private readonly AppDbContext _db;

    public DashboardQueries(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<RecognitionDayRow>> GetRecognitionsAsync(DateTime from, DateTime toExclusive, CancellationToken cancellationToken) =>
        await _db.RecognitionRequests.AsNoTracking()
            .Where(r => r.CreatedAt >= from && r.CreatedAt < toExclusive)
            .GroupBy(r => new { Day = r.CreatedAt.Date, r.Operation, r.Outcome })
            .Select(g => new RecognitionDayRow(g.Key.Day, g.Key.Operation, g.Key.Outcome, g.LongCount()))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<LedgerDayRow>> GetLedgerDaysAsync(Guid? clientId, DateTime from, DateTime toExclusive, CancellationToken cancellationToken)
    {
        var query = _db.LicenseTransactions.AsNoTracking()
            .Where(t => t.CreatedAt >= from && t.CreatedAt < toExclusive && (t.Type == LedgerEntryType.Consume || t.Type == LedgerEntryType.Refund));
        if (clientId is { } id)
        {
            query = query.Where(t => t.ClientId == id);
        }

        return await query
            .GroupBy(t => new { Day = t.CreatedAt.Date, t.Type, t.Operation })
            .Select(g => new LedgerDayRow(g.Key.Day, g.Key.Type, g.Key.Operation, g.LongCount(), g.Sum(t => (long)t.Credits)))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ApiDayRow>> GetApiDaysAsync(Guid? clientId, DateTime from, DateTime toExclusive, CancellationToken cancellationToken)
    {
        var query = _db.ApiRequestLogs.AsNoTracking().Where(l => l.CreatedAt >= from && l.CreatedAt < toExclusive);
        if (clientId is { } id)
        {
            query = query.Where(l => l.ClientId == id);
        }

        return await query
            .GroupBy(l => l.CreatedAt.Date)
            .Select(g => new ApiDayRow(g.Key, g.LongCount(), g.LongCount(l => l.StatusCode >= 400), g.LongCount(l => l.StatusCode >= 500)))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LatencyBucketRow>> GetLatencyHistogramAsync(
        Guid? clientId, DateTime from, DateTime toExclusive, int bucketMilliseconds, int capMilliseconds, CancellationToken cancellationToken)
    {
        var query = _db.ApiRequestLogs.AsNoTracking().Where(l => l.CreatedAt >= from && l.CreatedAt < toExclusive);
        if (clientId is { } id)
        {
            query = query.Where(l => l.ClientId == id);
        }

        // A percentile is not a SQL aggregate EF can express, so durations are counted in fixed-width buckets (one cheap GROUP BY) and
        // the percentile is read off the histogram in memory: exact to within one bucket, and bounded in size (days x buckets).
        return await query
            .GroupBy(l => new { Day = l.CreatedAt.Date, Bucket = (l.DurationMs > capMilliseconds ? capMilliseconds : l.DurationMs) / bucketMilliseconds })
            .Select(g => new LatencyBucketRow(g.Key.Day, g.Key.Bucket, g.LongCount()))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ApiKeyUsageRow>> GetTopApiKeysAsync(DateTime from, DateTime toExclusive, int take, CancellationToken cancellationToken)
    {
        var usage = await _db.ApiRequestLogs.AsNoTracking()
            .Where(l => l.CreatedAt >= from && l.CreatedAt < toExclusive && l.ApiKeyId != null)
            .GroupBy(l => l.ApiKeyId!.Value)
            .Select(g => new { KeyId = g.Key, Requests = g.LongCount(), Errors = g.LongCount(l => l.StatusCode >= 400), Last = g.Max(l => l.CreatedAt) })
            .OrderByDescending(x => x.Requests).ThenBy(x => x.KeyId)
            .Take(take)
            .ToListAsync(cancellationToken);

        var ids = usage.Select(u => u.KeyId).ToList();
        var keys = await _db.ApiKeys.AsNoTracking().Where(k => ids.Contains(k.Id))
            .Select(k => new { k.Id, k.Name, k.KeyPrefix })
            .ToDictionaryAsync(k => k.Id, cancellationToken);

        return usage
            .Where(u => keys.ContainsKey(u.KeyId))
            .Select(u => new ApiKeyUsageRow(u.KeyId, keys[u.KeyId].Name, keys[u.KeyId].KeyPrefix, u.Requests, u.Errors, u.Last))
            .ToList();
    }

    public async Task<IReadOnlyList<ClientStatusCount>> GetClientStatusCountsAsync(CancellationToken cancellationToken) =>
        await _db.Clients.AsNoTracking().Where(c => !c.IsSystem)
            .GroupBy(c => c.Status)
            .Select(g => new ClientStatusCount(g.Key, g.LongCount()))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<LicenseStatusCount>> GetLicenseStatusCountsAsync(DateTime now, CancellationToken cancellationToken) =>
        await _db.Licenses.AsNoTracking()
            .GroupBy(l => new { l.Status, PastEnd = l.ExpiresAt <= now })
            .Select(g => new LicenseStatusCount(g.Key.Status, g.Key.PastEnd, g.LongCount()))
            .ToListAsync(cancellationToken);

    public async Task<LicenseAttentionPage> GetExpiringLicensesAsync(DateTime now, DateTime until, int take, CancellationToken cancellationToken)
    {
        var query = from l in _db.Licenses.AsNoTracking()
                    join c in _db.Clients.AsNoTracking() on l.ClientId equals c.Id
                    where l.Status == LicenseStatus.Active && l.ExpiresAt > now && l.ExpiresAt <= until
                    select new { License = l, ClientName = c.Name };
        var count = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.License.ExpiresAt).ThenBy(x => x.License.Id).Take(take)
            .Select(x => new LicenseAttentionRow(x.License.Id, x.License.ClientId, x.ClientName, x.License.Name, x.License.TotalCredits, x.License.ConsumedCredits, x.License.ExpiresAt))
            .ToListAsync(cancellationToken);
        return new LicenseAttentionPage(count, items);
    }

    public async Task<LicenseAttentionPage> GetLowBalanceLicensesAsync(DateTime now, int percent, int take, CancellationToken cancellationToken)
    {
        var query = from l in _db.Licenses.AsNoTracking()
                    join c in _db.Clients.AsNoTracking() on l.ClientId equals c.Id
                    where l.Status == LicenseStatus.Active && l.StartsAt <= now && l.ExpiresAt > now && l.TotalCredits > 0
                          && (l.TotalCredits - l.ConsumedCredits) * 100L <= percent * (long)l.TotalCredits
                    select new { License = l, ClientName = c.Name };
        var count = await query.LongCountAsync(cancellationToken);
        var items = await query
            .OrderBy(x => (double)(x.License.TotalCredits - x.License.ConsumedCredits) / x.License.TotalCredits).ThenBy(x => x.License.Id)
            .Take(take)
            .Select(x => new LicenseAttentionRow(x.License.Id, x.License.ClientId, x.ClientName, x.License.Name, x.License.TotalCredits, x.License.ConsumedCredits, x.License.ExpiresAt))
            .ToListAsync(cancellationToken);
        return new LicenseAttentionPage(count, items);
    }

    public async Task<IReadOnlyList<TopClientRow>> GetTopClientsAsync(DateTime from, DateTime toExclusive, int take, CancellationToken cancellationToken)
    {
        var totals = await _db.LicenseTransactions.AsNoTracking()
            .Where(t => t.CreatedAt >= from && t.CreatedAt < toExclusive && t.Type == LedgerEntryType.Consume)
            .GroupBy(t => t.ClientId)
            .Select(g => new { ClientId = g.Key, Credits = g.Sum(t => -(long)t.Credits), Operations = g.LongCount() })
            .OrderByDescending(x => x.Credits).ThenBy(x => x.ClientId)
            .Take(take)
            .ToListAsync(cancellationToken);

        var ids = totals.Select(t => t.ClientId).ToList();
        var clients = await _db.Clients.AsNoTracking().Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Code, c.Name })
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        return totals
            .Where(t => clients.ContainsKey(t.ClientId))
            .Select(t => new TopClientRow(t.ClientId, clients[t.ClientId].Code, clients[t.ClientId].Name, t.Credits, t.Operations))
            .ToList();
    }

    public async Task<WebhookHealthDto> GetWebhookHealthAsync(DateTime from, DateTime toExclusive, CancellationToken cancellationToken)
    {
        var retrying = await _db.WebhookDeliveries.AsNoTracking().LongCountAsync(d => d.Status == DeliveryStatus.Pending && d.Attempts > 0, cancellationToken);
        var abandoned = await _db.WebhookDeliveries.AsNoTracking()
            .LongCountAsync(d => d.Status == DeliveryStatus.Abandoned && d.CreatedAt >= from && d.CreatedAt < toExclusive, cancellationToken);
        var delivered = await _db.WebhookDeliveries.AsNoTracking()
            .LongCountAsync(d => d.Status == DeliveryStatus.Delivered && d.CreatedAt >= from && d.CreatedAt < toExclusive, cancellationToken);
        var failing = await _db.WebhookEndpoints.AsNoTracking().LongCountAsync(e => e.Status == WebhookStatus.Active && e.FailureCount > 0, cancellationToken);
        var disabled = await _db.WebhookEndpoints.AsNoTracking().LongCountAsync(e => e.Status == WebhookStatus.Disabled, cancellationToken);
        return new WebhookHealthDto(retrying, abandoned, delivered, failing, disabled);
    }

    public async IAsyncEnumerable<ClientUsageRow> StreamClientUsageAsync(DateTime from, DateTime toExclusive, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rows = _db.RecognitionRequests.AsNoTracking()
            .Where(r => r.CreatedAt >= from && r.CreatedAt < toExclusive)
            .GroupBy(r => new { Day = r.CreatedAt.Date, r.Operation, r.Outcome })
            .OrderBy(g => g.Key.Day).ThenBy(g => g.Key.Operation).ThenBy(g => g.Key.Outcome)
            .Select(g => new ClientUsageRow(g.Key.Day, g.Key.Operation, g.Key.Outcome, g.LongCount(), g.Sum(r => (long)r.CreditsCharged)));
        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            yield return row;
        }
    }

    public async IAsyncEnumerable<AdminUsageRow> StreamAdminUsageAsync(DateTime start, DateTime toExclusive, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rows = from t in _db.LicenseTransactions.AsNoTracking()
                   join c in _db.Clients.AsNoTracking() on t.ClientId equals c.Id
                   where t.CreatedAt >= start && t.CreatedAt < toExclusive && (t.Type == LedgerEntryType.Consume || t.Type == LedgerEntryType.Refund)
                   group t by new { Day = t.CreatedAt.Date, ClientId = c.Id, c.Code, c.Name, t.Operation } into g
                   orderby g.Key.Day, g.Key.Code, g.Key.Operation
                   select new AdminUsageRow(
                       g.Key.Day, g.Key.ClientId, g.Key.Code, g.Key.Name, g.Key.Operation,
                       g.LongCount(t => t.Type == LedgerEntryType.Consume),
                       g.Sum(t => t.Type == LedgerEntryType.Consume ? -(long)t.Credits : 0L),
                       g.LongCount(t => t.Type == LedgerEntryType.Refund),
                       g.Sum(t => t.Type == LedgerEntryType.Refund ? (long)t.Credits : 0L));
        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            yield return row;
        }
    }
}
