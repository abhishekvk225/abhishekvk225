using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Api;
using NexaVerify.Application.Common;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Api;

namespace NexaVerify.Infrastructure.Platform;

/// <summary>Per-principal limits on expensive or abusable operations (<c>Throttle:*</c>, deploy/CONFIG.md). All windows are one UTC minute.</summary>
public sealed class ThrottleOptions
{
    public const string SectionName = "Throttle";

    /// <summary>Dashboard reads (several aggregate queries each).</summary>
    [Range(1, 100_000)]
    public int DashboardsPerMinute { get; set; } = 60;

    /// <summary>CSV exports (a report scan each).</summary>
    [Range(1, 100_000)]
    public int ExportsPerMinute { get; set; } = 6;

    /// <summary>"Send test event" clicks: each one makes the server call a client-chosen URL.</summary>
    [Range(1, 100_000)]
    public int WebhookTestsPerMinute { get; set; } = 10;

    /// <summary>Manual webhook delivery retries.</summary>
    [Range(1, 100_000)]
    public int WebhookRetriesPerMinute { get; set; } = 30;

    public int LimitFor(string policy) => policy switch
    {
        ThrottlePolicies.Dashboards => DashboardsPerMinute,
        ThrottlePolicies.Exports => ExportsPerMinute,
        ThrottlePolicies.WebhookTest => WebhookTestsPerMinute,
        ThrottlePolicies.WebhookRetry => WebhookRetriesPerMinute,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown throttle policy."),
    };
}

/// <summary>
/// Fixed one-minute window per (policy, principal), counted in the same shared counters as the API rate limits, so the limit holds
/// across nodes. The principal is the signed-in user or API key — never an IP address or anything the caller supplies.
/// </summary>
public sealed class PrincipalThrottle : IPrincipalThrottle
{
    private readonly SharedWindowCounters _counters;
    private readonly ThrottleOptions _options;
    private readonly TimeProvider _time;

    public PrincipalThrottle(SharedWindowCounters counters, IOptions<ThrottleOptions> options, TimeProvider time)
    {
        _counters = counters;
        _options = options.Value;
        _time = time;
    }

    public async Task<(Error? Error, TimeSpan RetryAfter)> TryAcquireAsync(string policy, Guid principalId, CancellationToken cancellationToken)
    {
        var limit = _options.LimitFor(policy);
        var now = _time.GetUtcNow().UtcDateTime;
        var bucket = now.Ticks / TimeSpan.TicksPerMinute;
        if (await _counters.TryTakeAsync(UsageCounterKinds.Throttle, KeyFor(policy, principalId), bucket, limit, cancellationToken))
        {
            return (null, TimeSpan.Zero);
        }

        var retry = TimeSpan.FromSeconds(60 - (now.Second + (now.Millisecond / 1000.0)));
        return (Error.TooManyRequests(ErrorCodes.RateLimited, "Too many requests for this operation. Retry after the indicated time."), retry);
    }

    /// <summary>A stable 128-bit key for (policy, principal); the table stores nothing readable.</summary>
    public static Guid KeyFor(string policy, Guid principalId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(policy + ":" + principalId.ToString("N")))[..16]);
}
