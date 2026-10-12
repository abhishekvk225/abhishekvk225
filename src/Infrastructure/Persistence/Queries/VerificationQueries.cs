using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Infrastructure.Persistence.Queries;

/// <summary>
/// Metadata-only reads over <c>verification.Requests</c> / <c>Events</c> (tenant-or-platform tables: no join to a strict table, no personal data).
/// Aggregations are pushed to SQL; the platform-wide period scans are served by <c>IX_Requests_Stats</c>, tenant ones by <c>(ClientId, CreatedAt)</c>.
/// </summary>
internal sealed class VerificationAdminQueries : IVerificationAdminQueries
{
    private const int MaxPage = 200;
    private readonly AppDbContext _db;

    public VerificationAdminQueries(AppDbContext db)
    {
        _db = db;
    }

    public async Task<VerificationStatsDto> GetStatsAsync(VerificationStatsQuery query, DateTime now, CancellationToken cancellationToken)
    {
        var scope = Scoped(query);
        if (scope is null)
        {
            return new VerificationStatsDto(0, [], [], 0, [], [], 0, 0, null, null, 0, null);
        }

        var inPeriod = InPeriod(scope, query.From, query.To);

        var total = await inPeriod.CountAsync(cancellationToken);
        var byType = await inPeriod.GroupBy(r => r.Type).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var byStatus = await inPeriod.GroupBy(r => r.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var review = await inPeriod.CountAsync(r => r.ReviewRequired, cancellationToken);
        var failures = await inPeriod.Where(r => r.Status == VerificationStatus.Failed)
            .GroupBy(r => r.ErrorCode).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var providers = await inPeriod.Where(r => r.ExternalState != ExternalState.NotRequested)
            .GroupBy(r => r.ExternalState).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var credits = await inPeriod
            .GroupBy(_ => 1)
            .Select(g => new { Extraction = g.Sum(r => (long)r.CreditsCharged), Provider = g.Sum(r => (long)r.ProviderCreditsCharged) })
            .FirstOrDefaultAsync(cancellationToken);

        var timed = inPeriod.Where(r => r.StartedAt != null && r.CompletedAt != null && r.Status != VerificationStatus.Cancelled && r.Status != VerificationStatus.Expired);
        var timedCount = await timed.CountAsync(cancellationToken);
        var p50 = await PercentileSecondsAsync(timed, timedCount, 0.50, cancellationToken);
        var p95 = await PercentileSecondsAsync(timed, timedCount, 0.95, cancellationToken);

        var queued = scope.Where(r => r.Status == VerificationStatus.Queued);
        var depth = await queued.CountAsync(cancellationToken);
        var oldest = depth == 0 ? null : await queued.MinAsync(r => (DateTime?)r.CreatedAt, cancellationToken);

        return new VerificationStatsDto(
            total,
            byType.OrderBy(x => x.Key).Select(x => new VerificationCountDto(x.Key.ToString(), x.Count)).ToList(),
            byStatus.OrderBy(x => x.Key).Select(x => new VerificationCountDto(x.Key.ToString(), x.Count)).ToList(),
            review,
            failures.OrderByDescending(x => x.Count).Select(x => new VerificationCountDto(x.Key ?? "UNKNOWN", x.Count)).ToList(),
            providers.OrderBy(x => x.Key).Select(x => new VerificationCountDto(x.Key.ToString(), x.Count)).ToList(),
            credits?.Extraction ?? 0,
            credits?.Provider ?? 0,
            p50,
            p95,
            depth,
            oldest is { } o ? Math.Max(0, (now - o).TotalSeconds) : null);
    }

    public async Task<IReadOnlyList<VerificationTimePoint>> GetTimeSeriesAsync(VerificationStatsQuery query, CancellationToken cancellationToken)
    {
        var scope = Scoped(query);
        if (scope is null)
        {
            return [];
        }

        var rows = await InPeriod(scope, query.From, query.To)
            .GroupBy(r => new { Day = r.CreatedAt.Date, r.Type })
            .Select(g => new
            {
                g.Key.Day,
                g.Key.Type,
                Total = g.Count(),
                Completed = g.Count(r => r.Status == VerificationStatus.Completed),
                Review = g.Count(r => r.Status == VerificationStatus.ReviewRequired),
                Failed = g.Count(r => r.Status == VerificationStatus.Failed),
                Credits = g.Sum(r => (long)r.CreditsCharged + r.ProviderCreditsCharged),
            })
            .OrderBy(x => x.Day).ThenBy(x => x.Type)
            .Take(5000)
            .ToListAsync(cancellationToken);
        return rows.Select(x => new VerificationTimePoint(DateOnly.FromDateTime(x.Day), x.Type, x.Total, x.Completed, x.Review, x.Failed, x.Credits)).ToList();
    }

    public async Task<IReadOnlyList<ProviderOutcomeRow>> GetProviderOutcomesAsync(DateTime since, CancellationToken cancellationToken)
    {
        var rows = await _db.VerificationRequests.AsNoTracking()
            .Where(r => r.ExternalProviderId != null && r.ExternalCompletedAt >= since)
            .GroupBy(r => new { r.ExternalProviderId, r.ExternalState })
            .Select(g => new { g.Key.ExternalProviderId, g.Key.ExternalState, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.ExternalProviderId!).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            int Count(ExternalState s) => g.Where(x => x.ExternalState == s).Sum(x => x.Count);
            return new ProviderOutcomeRow(
                g.Key,
                new ProviderOutcomeCountsDto(Count(ExternalState.Verified), Count(ExternalState.NotVerified), Count(ExternalState.Inconclusive), Count(ExternalState.ProviderError), Count(ExternalState.Timeout)));
        }).ToList();
    }

    public async Task<(IReadOnlyList<AdminVerificationItemDto> Items, int Total)> ListForClientAsync(Guid clientId, AdminVerificationListQuery query, CancellationToken cancellationToken)
    {
        var requests = _db.VerificationRequests.AsNoTracking().Where(r => r.ClientId == clientId);
        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            if (!Enum.TryParse<VerificationType>(query.Type, true, out var type))
            {
                return ([], 0);
            }

            requests = requests.Where(r => r.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<VerificationStatus>(query.Status, true, out var status))
            {
                return ([], 0);
            }

            requests = requests.Where(r => r.Status == status);
        }

        requests = InPeriod(requests, query.From, query.To);
        var total = await requests.CountAsync(cancellationToken);
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, MaxPage);
        var rows = await requests.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id).Skip((page - 1) * size).Take(size)
            .Select(r => new
            {
                r.Id, r.ClientId, r.Type, r.Status, r.UploadState, r.ExtractionState, r.RulesState, r.ConsistencyState, r.ExternalState, r.ExternalProviderId, r.ReviewRequired,
                r.Assurance, r.ErrorCode, r.CreditsCharged, r.ProviderCreditsCharged, r.Attempts, r.CorrelationId, r.CreatedAt, r.StartedAt, r.CompletedAt,
            })
            .ToListAsync(cancellationToken);
        return (rows.Select(r => new AdminVerificationItemDto(
            r.Id, r.ClientId, r.Type.ToString(), r.Status.ToString(),
            new VerificationStagesDto(r.UploadState.ToString(), r.ExtractionState.ToString(), r.RulesState.ToString(), r.ConsistencyState.ToString(), r.ExternalState.ToString(), r.ExternalProviderId),
            r.ReviewRequired, r.Assurance.ToString(), r.ErrorCode, r.CreditsCharged, r.ProviderCreditsCharged, r.Attempts, r.CorrelationId, r.CreatedAt, r.StartedAt, r.CompletedAt)).ToList(), total);
    }

    public async Task<(IReadOnlyList<NexaVerify.Domain.Auditing.AuditLog> Items, int Total)> ListPlatformAuditAsync(
        DateTime? from, DateTime? to, int skip, int take, CancellationToken cancellationToken)
    {
        // Platform-level verification events (module switches, administrative views) are recorded under the platform tenant.
        var query = _db.AuditLogs.AsNoTracking().Where(a => a.ClientId == NexaVerify.Domain.Common.PlatformTenant.ClientId && a.Action.StartsWith("verification."));
        if (from is { } f)
        {
            query = query.Where(a => a.OccurredAt >= f);
        }

        if (to is { } t)
        {
            query = query.Where(a => a.OccurredAt <= t);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(a => a.Id).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, MaxPage)).ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<AdminVerificationDetailDto?> GetDetailAsync(Guid clientId, Guid requestId, CancellationToken cancellationToken)
    {
        var r = await _db.VerificationRequests.AsNoTracking().Where(x => x.ClientId == clientId && x.Id == requestId)
            .Select(x => new
            {
                x.Id, x.ClientId, x.Type, x.Status, x.UploadState, x.ExtractionState, x.RulesState, x.ConsistencyState, x.ExternalState, x.ExternalProviderId, x.ReviewRequired,
                x.Assurance, x.ErrorCode, x.CreditsCharged, x.ProviderCreditsCharged, x.Attempts, x.CorrelationId, x.CreatedAt, x.StartedAt, x.CompletedAt,
            }).FirstOrDefaultAsync(cancellationToken);
        if (r is null)
        {
            return null;
        }

        var events = await _db.VerificationEvents.AsNoTracking().Where(e => e.ClientId == clientId && e.RequestId == requestId).OrderBy(e => e.Id).Take(500)
            .Select(e => new { e.FromStatus, e.ToStatus, e.Stage, e.NoteCode, e.ActorType, e.CreatedAt }).ToListAsync(cancellationToken);
        var item = new AdminVerificationItemDto(
            r.Id, r.ClientId, r.Type.ToString(), r.Status.ToString(),
            new VerificationStagesDto(r.UploadState.ToString(), r.ExtractionState.ToString(), r.RulesState.ToString(), r.ConsistencyState.ToString(), r.ExternalState.ToString(), r.ExternalProviderId),
            r.ReviewRequired, r.Assurance.ToString(), r.ErrorCode, r.CreditsCharged, r.ProviderCreditsCharged, r.Attempts, r.CorrelationId, r.CreatedAt, r.StartedAt, r.CompletedAt);
        return new AdminVerificationDetailDto(item, events.Select(e => new VerificationEventDto(e.FromStatus?.ToString(), e.ToStatus.ToString(), e.Stage, e.NoteCode, e.ActorType.ToString(), e.CreatedAt)).ToList());
    }

    private static IQueryable<VerificationRequest> InPeriod(IQueryable<VerificationRequest> q, DateTime? from, DateTime? to)
    {
        if (from is { } f)
        {
            q = q.Where(r => r.CreatedAt >= f);
        }

        if (to is { } t)
        {
            q = q.Where(r => r.CreatedAt <= t);
        }

        return q;
    }

    /// <summary>Type/client narrowing; null when the requested type string is not a known type (an unknown type has no rows).</summary>
    private IQueryable<VerificationRequest>? Scoped(VerificationStatsQuery query)
    {
        var q = _db.VerificationRequests.AsNoTracking().AsQueryable();
        if (query.ClientId is { } clientId)
        {
            q = q.Where(r => r.ClientId == clientId);
        }

        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            if (!Enum.TryParse<VerificationType>(query.Type, true, out var type))
            {
                return null;
            }

            q = q.Where(r => r.Type == type);
        }

        return q;
    }

    private static async Task<double?> PercentileSecondsAsync(IQueryable<VerificationRequest> timed, int count, double percentile, CancellationToken cancellationToken)
    {
        if (count == 0)
        {
            return null;
        }

        var index = Math.Max(0, (int)Math.Ceiling(percentile * count) - 1);
        var millis = await timed.OrderBy(r => EF.Functions.DateDiffMillisecond(r.StartedAt!.Value, r.CompletedAt!.Value))
            .Skip(index).Select(r => (long?)EF.Functions.DateDiffMillisecond(r.StartedAt!.Value, r.CompletedAt!.Value)).FirstOrDefaultAsync(cancellationToken);
        return millis is { } m ? m / 1000.0 : null;
    }
}
