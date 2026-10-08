using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Api;
using NexaVerify.Domain.Api;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Background;

public sealed class ApiLogOptions
{
    public const string SectionName = "ApiLogs";

    /// <summary>How many days of request logs to keep.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Most entries waiting to be written; beyond this, new entries are dropped (and counted) rather than slowing the API.</summary>
    public int QueueCapacity { get; set; } = 20_000;

    public int FlushIntervalMilliseconds { get; set; } = 1000;
}

/// <summary>Queues request-log entries and writes them in batches; also purges rows older than the retention window.</summary>
public sealed class ApiRequestLogWriter : BackgroundService, IApiRequestLogSink
{
    private readonly Channel<ApiRequestLogEntry> _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ApiRequestLogWriter> _logger;
    private readonly ApiLogOptions _options;
    private readonly TimeProvider _time;
    private long _dropped;
    private DateTime _lastPurge = DateTime.MinValue;

    public ApiRequestLogWriter(IServiceScopeFactory scopes, IOptions<ApiLogOptions> options, ILogger<ApiRequestLogWriter> logger, TimeProvider time)
    {
        _scopes = scopes;
        _logger = logger;
        _options = options.Value;
        _time = time;
        _queue = Channel.CreateBounded<ApiRequestLogEntry>(new BoundedChannelOptions(Math.Max(100, _options.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
    }

    public void Enqueue(ApiRequestLogEntry entry)
    {
        if (!_queue.Writer.TryWrite(entry) && Interlocked.Increment(ref _dropped) % 1000 == 1)
        {
            _logger.LogWarning("API request log queue is full; entries are being dropped ({Dropped} so far)", Interlocked.Read(ref _dropped));
        }
    }

    /// <summary>Writes everything currently queued. Used by the background loop and by tests.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        while (_queue.Reader.TryPeek(out _))
        {
            var batch = new List<ApiRequestLogEntry>(500);
            while (batch.Count < 500 && _queue.Reader.TryRead(out var entry))
            {
                batch.Add(entry);
            }

            if (batch.Count == 0)
            {
                return;
            }

            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("api request log write");
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.ApiRequestLogs.AddRange(batch.Select(e => ApiRequestLog.Create(
                    e.ClientId, e.ApiKeyId, e.UserId, e.Method, e.Route, e.StatusCode, e.DurationMs, e.RequestBytes, e.IpAddress, e.UserAgent,
                    e.ErrorCode, e.CorrelationId, e.OccurredAt)));
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not write {Count} API request log entries", batch.Count);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(100, _options.FlushIntervalMilliseconds));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync(stoppingToken);
                await PurgeAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down: flush what is left below
        }

        await FlushAsync(CancellationToken.None);
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (now - _lastPurge < TimeSpan.FromHours(1))
        {
            return;
        }

        _lastPurge = now;
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("api request log retention");
            var cutoff = now.AddDays(-Math.Max(1, _options.RetentionDays));
            var removed = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ApiRequestLogs
                .Where(l => l.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
            if (removed > 0)
            {
                _logger.LogInformation("Purged {Count} API request log rows older than {Cutoff:u}", removed, cutoff);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "API request log purge failed");
        }
    }
}
