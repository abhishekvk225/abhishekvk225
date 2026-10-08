using NexaVerify.Contracts.Dashboards;
using NexaVerify.Domain.Faces;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Application.Dashboards;

public sealed record RecognitionDayRow(DateTime Day, MeteredOperation Operation, RecognitionOutcome Outcome, long Count);

/// <summary>Ledger rows of type Consume or Refund for one day. <c>Credits</c> is the signed ledger sum (consumptions are negative).</summary>
public sealed record LedgerDayRow(DateTime Day, LedgerEntryType Type, MeteredOperation? Operation, long Entries, long Credits);

public sealed record ApiDayRow(DateTime Day, long Requests, long Errors, long ServerErrors);

public sealed record LatencyBucketRow(DateTime Day, int Bucket, long Count);

public sealed record ApiKeyUsageRow(Guid ApiKeyId, string Name, string KeyPrefix, long Requests, long Errors, DateTime? LastRequestAt);

public sealed record ClientStatusCount(ClientStatus Status, long Count);

/// <summary>Licenses with one stored status; <c>PastEnd</c> says whether their end date has passed (the effective status is derived from both).</summary>
public sealed record LicenseStatusCount(LicenseStatus Status, bool PastEnd, long Count);

public sealed record LicenseAttentionRow(Guid LicenseId, Guid ClientId, string ClientName, string LicenseName, int TotalCredits, int ConsumedCredits, DateTime ExpiresAt);

public sealed record LicenseAttentionPage(long Count, IReadOnlyList<LicenseAttentionRow> Items);

public sealed record TopClientRow(Guid ClientId, string ClientCode, string ClientName, long CreditsConsumed, long BilledOperations);

public sealed record ClientUsageRow(DateTime Day, MeteredOperation Operation, RecognitionOutcome Outcome, long Requests, long CreditsCharged);

public sealed record AdminUsageRow(
    DateTime Day, Guid ClientId, string ClientCode, string ClientName, MeteredOperation? Operation,
    long BilledOperations, long CreditsConsumed, long Refunds, long CreditsRefunded);

/// <summary>
/// Read-only, projection-only aggregate queries (SQL GROUP BY, no entity materialisation). Every method runs in the caller's tenant
/// scope: a client sees only its own rows; platform scope sees all tenants' NON-strict data. Face tables are strictly tenant-owned,
/// so recognition outcomes are only available to a tenant scope — platform dashboards use the ledger and the API request log.
/// </summary>
public interface IDashboardQueries
{
    /// <summary>Tenant scope only (biometric request history is strict).</summary>
    Task<IReadOnlyList<RecognitionDayRow>> GetRecognitionsAsync(DateTime from, DateTime toExclusive, CancellationToken cancellationToken);

    Task<IReadOnlyList<LedgerDayRow>> GetLedgerDaysAsync(Guid? clientId, DateTime from, DateTime toExclusive, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApiDayRow>> GetApiDaysAsync(Guid? clientId, DateTime from, DateTime toExclusive, CancellationToken cancellationToken);

    Task<IReadOnlyList<LatencyBucketRow>> GetLatencyHistogramAsync(
        Guid? clientId, DateTime from, DateTime toExclusive, int bucketMilliseconds, int capMilliseconds, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApiKeyUsageRow>> GetTopApiKeysAsync(DateTime from, DateTime toExclusive, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<ClientStatusCount>> GetClientStatusCountsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<LicenseStatusCount>> GetLicenseStatusCountsAsync(DateTime now, CancellationToken cancellationToken);

    Task<LicenseAttentionPage> GetExpiringLicensesAsync(DateTime now, DateTime until, int take, CancellationToken cancellationToken);

    Task<LicenseAttentionPage> GetLowBalanceLicensesAsync(DateTime now, int percent, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<TopClientRow>> GetTopClientsAsync(DateTime from, DateTime toExclusive, int take, CancellationToken cancellationToken);

    Task<WebhookHealthDto> GetWebhookHealthAsync(DateTime from, DateTime toExclusive, CancellationToken cancellationToken);

    /// <summary>Tenant scope only. One row per (day, operation, outcome), streamed.</summary>
    IAsyncEnumerable<ClientUsageRow> StreamClientUsageAsync(DateTime from, DateTime toExclusive, CancellationToken cancellationToken);

    /// <summary>Platform scope. One row per (day, client, operation) from the ledger, streamed.</summary>
    IAsyncEnumerable<AdminUsageRow> StreamAdminUsageAsync(DateTime from, DateTime toExclusive, CancellationToken cancellationToken);
}
