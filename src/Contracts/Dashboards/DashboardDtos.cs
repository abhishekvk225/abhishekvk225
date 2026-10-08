using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Licensing;

namespace NexaVerify.Contracts.Dashboards;

/// <summary>Window for a dashboard, in whole UTC days ending today. Out-of-range values are rejected, not silently clamped.</summary>
public sealed record DashboardQuery
{
    public int? Days { get; init; }
}

/// <summary>Inclusive UTC date range for an export. Both bounds are optional (default: the last 30 days).</summary>
public sealed record UsageReportQuery
{
    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }
}

public sealed record CountByLabelDto(string Label, long Count);

public sealed record RecognitionSummaryDto(long Total, long Successful, long NoMatch, long Failed, decimal SuccessRate, decimal NoMatchRate, decimal ErrorRate);

public sealed record RecognitionDayDto(DateOnly Date, long Total, long Successful, long NoMatch, long Failed);

public sealed record RecognitionBreakdownDto(DateOnly Date, string Operation, string Outcome, long Count);

public sealed record CreditSummaryDto(long Consumed, long Refunded, long Net);

public sealed record CreditDayDto(DateOnly Date, long Consumed, long Refunded, long Net);

public sealed record ApiDayDto(DateOnly Date, long Requests, long Errors, long ServerErrors, int? P95LatencyMs);

public sealed record ApiSummaryDto(long Requests, long Errors, long ServerErrors, decimal ErrorRate, int? P95LatencyMs);

public sealed record TopApiKeyDto(Guid ApiKeyId, string Name, string KeyPrefix, long Requests, long Errors, DateTime? LastRequestAt);

/// <summary>The client's own numbers. Credits come from the ledger, so they always agree with the credit history.</summary>
public sealed record ClientDashboardDto(
    DateTime From,
    DateTime To,
    int Days,
    LicenseSummaryDto Licenses,
    RecognitionSummaryDto Recognitions,
    IReadOnlyList<RecognitionDayDto> RecognitionsPerDay,
    IReadOnlyList<RecognitionBreakdownDto> RecognitionBreakdown,
    CreditSummaryDto Credits,
    IReadOnlyList<CreditDayDto> CreditsPerDay,
    ApiSummaryDto Api,
    IReadOnlyList<ApiDayDto> ApiPerDay,
    IReadOnlyList<TopApiKeyDto> TopApiKeys);

public sealed record LicenseAttentionDto(
    Guid LicenseId, Guid ClientId, string ClientName, string LicenseName, long RemainingCredits, long TotalCredits, int PercentRemaining, DateTime ExpiresAt);

public sealed record LicenseAttentionListDto(int Threshold, long Count, IReadOnlyList<LicenseAttentionDto> Items);

public sealed record TopClientDto(Guid ClientId, string ClientCode, string ClientName, long CreditsConsumed, long BilledOperations);

public sealed record BilledOperationDto(DateOnly Date, string Operation, long Count);

public sealed record WebhookHealthDto(long RetryingDeliveries, long AbandonedInPeriod, long DeliveredInPeriod, long EndpointsFailing, long EndpointsDisabled);

/// <summary>
/// Platform-wide aggregates only. Recognition volume is derived from the credit ledger and the API request log, never from face
/// tables, so nothing here can identify a person.
/// </summary>
public sealed record AdminDashboardDto(
    DateTime From,
    DateTime To,
    int Days,
    IReadOnlyList<CountByLabelDto> ClientsByStatus,
    IReadOnlyList<CountByLabelDto> LicensesByStatus,
    LicenseAttentionListDto ExpiringLicenses,
    LicenseAttentionListDto LowBalanceLicenses,
    CreditSummaryDto Credits,
    IReadOnlyList<CreditDayDto> CreditsPerDay,
    IReadOnlyList<BilledOperationDto> BilledOperationsPerDay,
    IReadOnlyList<TopClientDto> TopClients,
    ApiSummaryDto Api,
    IReadOnlyList<ApiDayDto> ApiPerDay,
    WebhookHealthDto Webhooks);

public sealed record NotificationDto(Guid Id, string Type, string Severity, string Title, string Message, DateTime CreatedAt, bool IsRead);

public sealed record NotificationFeedDto(long UnreadCount, PagedResult<NotificationDto> Notifications);

public sealed record NotificationListQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;

    public bool UnreadOnly { get; init; }
}
