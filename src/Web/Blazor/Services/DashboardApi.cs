namespace NexaVerify.Web.Services;

public enum DashboardRange
{
    Last7Days = 7,
    Last30Days = 30,
    Last90Days = 90,
}

/// <summary>One shared time range drives every dashboard widget (docs/06 §2).</summary>
public sealed class DashboardRangeState
{
    public DashboardRange Range { get; private set; } = DashboardRange.Last30Days;

    public event Action? Changed;

    public int Days => (int)Range;

    public void Set(DashboardRange range)
    {
        if (range == Range)
        {
            return;
        }

        Range = range;
        Changed?.Invoke();
    }
}

public sealed record KpiModel(string Title, string Value, string Icon, double? DeltaPercent, bool HigherIsBetter, IReadOnlyList<double>? Sparkline);

public sealed record TrendPoint(string Label, double Value);

public sealed record TrendSeries(string Name, IReadOnlyList<TrendPoint> Points);

public sealed record ClientUsageRow(string Client, long Requests, long Credits);

public sealed record ExpiringLicenseRow(string Client, string LicenseNumber, DateTimeOffset ExpiresAt, long Remaining, long Total);

public sealed record AlertModel(string Severity, string Message);

public sealed record StatusCount(string Label, long Count);

public sealed record AdminDashboardModel(
    IReadOnlyList<KpiModel> Kpis,
    IReadOnlyList<TrendSeries> RequestsTrend,
    IReadOnlyList<TrendSeries> CreditsTrend,
    IReadOnlyList<ClientUsageRow> ClientUsage,
    IReadOnlyList<ExpiringLicenseRow> ExpiringLicenses,
    IReadOnlyList<ExpiringLicenseRow> LowBalanceLicenses,
    IReadOnlyList<StatusCount> ClientsByStatus,
    IReadOnlyList<AlertModel> Alerts);

public sealed record OutcomeSplit(long Successful, long NoMatch, long Failed);

public sealed record RecognitionRow(string Id, DateTimeOffset At, string Operation, string Outcome, string Count);

public sealed record ApiUsageModel(long Calls, double ErrorRatePercent, int? P95LatencyMs);

public sealed record ClientDashboardModel(
    long CreditsRemaining,
    long CreditsTotal,
    DateTimeOffset? LicenseExpiresAt,
    string? PlanName,
    string LicenseMessage,
    IReadOnlyList<KpiModel> Kpis,
    OutcomeSplit Outcomes,
    IReadOnlyList<TrendSeries> UsageTrend,
    IReadOnlyList<RecognitionRow> RecentResults,
    ApiUsageModel ApiUsage,
    IReadOnlyList<AlertModel> Alerts);

/// <summary>Typed client for the dashboard endpoints (docs/03 sections 3.3, 4, 6).</summary>
public interface IDashboardApiClient
{
    Task<ApiResult<AdminDashboardModel>> GetAdminDashboardAsync(DashboardRange range, CancellationToken ct = default);

    Task<ApiResult<ClientDashboardModel>> GetClientDashboardAsync(DashboardRange range, CancellationToken ct = default);
}
