using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Infrastructure.Platform;

namespace NexaVerify.Infrastructure.Background;

/// <summary>
/// Deletes shared counter buckets that can no longer matter (minute and throttle windows after an hour, daily windows after a few
/// days). Idempotent, so every node may run it; the delete is batched.
/// </summary>
public sealed class UsageCounterPurger : BackgroundService
{
    private readonly ICounterBackend _backend;
    private readonly SharedCounterOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<UsageCounterPurger> _logger;

    public UsageCounterPurger(ICounterBackend backend, IOptions<SharedCounterOptions> options, TimeProvider time, ILogger<UsageCounterPurger> logger)
    {
        _backend = backend;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Shared)
        {
            return;
        }

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.PurgeIntervalMinutes));
            do
            {
                await PurgeOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    /// <summary>One purge pass; never throws. Returns the number of rows removed. Public for tests.</summary>
    public async Task<int> PurgeOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            return await _backend.PurgeAsync(now.AddMinutes(-_options.ShortBucketRetentionMinutes), now.AddDays(-_options.DailyBucketRetentionDays), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Purging old rate-limit counters failed; it will be retried");
            return 0;
        }
    }
}
