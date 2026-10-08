using System.ComponentModel.DataAnnotations;

namespace NexaVerify.Application.Dashboards;

/// <summary>Limits and thresholds of the dashboards and usage reports (nothing about them is hard-coded in the services).</summary>
public sealed class DashboardOptions
{
    public const string SectionName = "Dashboards";

    [Range(1, 365)]
    public int DefaultDays { get; set; } = 30;

    /// <summary>Longest dashboard window a caller may ask for.</summary>
    [Range(1, 365)]
    public int MaxDays { get; set; } = 90;

    [Range(1, 50)]
    public int TopApiKeys { get; set; } = 5;

    [Range(1, 100)]
    public int TopClients { get; set; } = 10;

    /// <summary>How many licenses the "expiring" and "low balance" lists show (the counts are always complete).</summary>
    [Range(1, 100)]
    public int AttentionListSize { get; set; } = 10;

    [Range(1, 365)]
    public int ExpiringWithinDays { get; set; } = 30;

    /// <summary>A license with this share of its credits left (or less) is "low balance".</summary>
    [Range(1, 99)]
    public int LowBalancePercent { get; set; } = 10;

    /// <summary>Resolution of the latency percentile: durations are counted in buckets of this width, so p95 is exact to within one bucket.</summary>
    [Range(1, 1000)]
    public int LatencyBucketMilliseconds { get; set; } = 25;

    /// <summary>Durations above this are counted in the last bucket.</summary>
    [Range(100, 600_000)]
    public int LatencyCapMilliseconds { get; set; } = 10_000;

    [Range(1, 366)]
    public int DefaultReportDays { get; set; } = 30;

    /// <summary>Longest range one usage export may cover (bounds the cost of a single request).</summary>
    [Range(1, 366)]
    public int MaxReportDays { get; set; } = 92;
}
