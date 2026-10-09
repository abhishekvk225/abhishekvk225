using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Billing;

namespace NexaVerify.Infrastructure.Background;

/// <summary>
/// Housekeeping for online payments. For every Pending order older than <c>Billing:ReconcileAfterMinutes</c> it asks the payment provider
/// what happened (a lost or late webhook would otherwise leave a paying customer without credits), and an order that is still unpaid after
/// <c>Billing:PendingExpiryHours</c> is closed and marked Expired. Each order is handled in its own scope and transaction, so one failure
/// never blocks the rest; the work is idempotent, so several API nodes can run it.
/// </summary>
public sealed partial class BillingMaintenanceProcessor
{
    private readonly IServiceScopeFactory _scopes;
    private readonly BillingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<BillingMaintenanceProcessor> _logger;

    public BillingMaintenanceProcessor(IServiceScopeFactory scopes, IOptions<BillingOptions> options, TimeProvider time, ILogger<BillingMaintenanceProcessor> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>One pass; returns how many orders changed state (granted, failed or expired). Public so tests can drive it without the timer.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return 0;
        }

        var cutoff = _time.GetUtcNow().UtcDateTime.AddMinutes(-_options.ReconcileAfterMinutes);
        IReadOnlyList<(Guid Id, Guid ClientId)> due;
        await using (var scope = _scopes.CreateAsyncScope())
        {
            using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("billing maintenance: list unpaid orders");
            due = await scope.ServiceProvider.GetRequiredService<Application.Billing.IPaymentOrderRepository>()
                .ListPendingCreatedBeforeAsync(cutoff, _options.JobBatchSize, cancellationToken);
        }

        var changed = 0;
        foreach (var (id, clientId) in due)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("billing maintenance: reconcile one order");
                var result = await scope.ServiceProvider.GetRequiredService<IPaymentReconciler>().ReconcileOrExpireAsync(id, cancellationToken);
                if (result.IsSuccess && result.Value is { } outcome)
                {
                    changed++;
                    LogOrder(id, clientId, outcome);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(ex, id);
            }
        }

        if (due.Count > 0)
        {
            LogRun(due.Count, changed);
        }

        return changed;
    }

    [LoggerMessage(EventId = 9015, Level = LogLevel.Information, Message = "Billing maintenance looked at {Orders} unpaid order(s); {Changed} changed")]
    private partial void LogRun(int orders, int changed);

    [LoggerMessage(EventId = 9009, Level = LogLevel.Information, Message = "Billing maintenance: order {OrderId} of client {ClientId}: {Outcome}")]
    private partial void LogOrder(Guid orderId, Guid clientId, string outcome);

    [LoggerMessage(EventId = 9018, Level = LogLevel.Error, Message = "Billing maintenance failed for order {OrderId}")]
    private partial void LogFailed(Exception exception, Guid orderId);
}

/// <summary>Runs <see cref="BillingMaintenanceProcessor"/> on a timer (<c>Billing:JobIntervalMinutes</c>).</summary>
public sealed class BillingMaintenanceJob : BackgroundService
{
    private readonly BillingMaintenanceProcessor _processor;
    private readonly BillingOptions _options;
    private readonly ILogger<BillingMaintenanceJob> _logger;

    public BillingMaintenanceJob(BillingMaintenanceProcessor processor, IOptions<BillingOptions> options, ILogger<BillingMaintenanceJob> logger)
    {
        _processor = processor;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Billing maintenance job is idle (Billing:Enabled=false)");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.JobInitialDelaySeconds), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.JobIntervalMinutes));
            do
            {
                try
                {
                    await _processor.RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Billing maintenance run failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }
}
