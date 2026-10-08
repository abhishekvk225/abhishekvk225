using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Licensing;

namespace NexaVerify.Infrastructure.Background;

/// <summary>
/// Finds licenses and API keys that are due for an alert (platform scope, read-only) and raises the alerts inside each owning
/// client's own tenant scope: one transaction per alert stores it and stages its webhook event. De-duplication is the unique
/// (subject, type, bucket) index, so restarts, overlapping runs and several nodes cannot send the same alert twice.
/// </summary>
public sealed class LicenseAlertProcessor
{
    private readonly IServiceScopeFactory _scopes;
    private readonly LicenseAlertOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<LicenseAlertProcessor> _logger;

    public LicenseAlertProcessor(IServiceScopeFactory scopes, IOptions<LicenseAlertOptions> options, TimeProvider time, ILogger<LicenseAlertProcessor> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>One pass; returns how many new alerts were raised. Public so tests can drive it without the timer.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var licenses = new List<LicenseAlertCandidate>();
        var keys = new List<ApiKeyAlertCandidate>();

        await using (var scope = _scopes.CreateAsyncScope())
        {
            using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("license alerts: list candidates");
            var candidates = scope.ServiceProvider.GetRequiredService<ILicenseAlertCandidates>();

            Guid? after = null;
            while (true)
            {
                var page = await candidates.ListLicensesAsync(now, _options, after, _options.BatchSize, cancellationToken);
                licenses.AddRange(page);
                if (page.Count < _options.BatchSize)
                {
                    break;
                }

                after = page[^1].LicenseId;
            }

            after = null;
            while (true)
            {
                var page = await candidates.ListApiKeysAsync(now, _options, after, _options.BatchSize, cancellationToken);
                keys.AddRange(page);
                if (page.Count < _options.BatchSize)
                {
                    break;
                }

                after = page[^1].ApiKeyId;
            }
        }

        var clientIds = licenses.Select(l => l.ClientId).Concat(keys.Select(k => k.ClientId)).Distinct().ToList();
        var raised = 0;
        foreach (var clientId in clientIds)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginTenant(clientId);
                raised += await scope.ServiceProvider.GetRequiredService<ILicenseAlertService>().RaiseDueAsync(
                    licenses.Where(l => l.ClientId == clientId).ToList(), keys.Where(k => k.ClientId == clientId).ToList(), now, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "License alerts failed for client {ClientId}", clientId);
            }
        }

        if (raised > 0)
        {
            _logger.LogInformation("Raised {Count} license alert(s)", raised);
        }

        return raised;
    }
}

/// <summary>Runs <see cref="LicenseAlertProcessor"/> on a timer (hourly by default).</summary>
public sealed class LicenseAlertJob : BackgroundService
{
    private readonly LicenseAlertProcessor _processor;
    private readonly LicenseAlertOptions _options;
    private readonly ILogger<LicenseAlertJob> _logger;

    public LicenseAlertJob(LicenseAlertProcessor processor, IOptions<LicenseAlertOptions> options, ILogger<LicenseAlertJob> logger)
    {
        _processor = processor;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("License alert job is disabled ({Key}:Enabled=false)", LicenseAlertOptions.SectionName);
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.InitialDelaySeconds), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.IntervalMinutes));
            do
            {
                try
                {
                    await _processor.RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "License alert run failed");
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
