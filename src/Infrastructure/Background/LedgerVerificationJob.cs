using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Licensing;

namespace NexaVerify.Infrastructure.Background;

/// <summary>
/// Nightly tamper check: recomputes every license's ledger hash chain and balance (platform scope, read-only apart from the audit
/// entry written when something is wrong). A finding is logged at Critical by the verification service. Also runnable on demand
/// through the admin endpoint; the two share a gate so they never overlap.
/// </summary>
public sealed class LedgerVerificationJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly LedgerVerificationOptions _options;
    private readonly ILogger<LedgerVerificationJob> _logger;

    public LedgerVerificationJob(IServiceScopeFactory scopes, IOptions<LedgerVerificationOptions> options, ILogger<LedgerVerificationJob> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Ledger verification job is disabled ({Key}:Enabled=false)", LedgerVerificationOptions.SectionName);
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(_options.InitialDelayMinutes), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(_options.IntervalHours));
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    /// <summary>One verification pass; never throws (a failing run must not stop the next one). Public for tests.</summary>
    public async Task<Application.Common.Result<Contracts.Licensing.LedgerVerificationReportDto>?> RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("ledger verification");
            var result = await scope.ServiceProvider.GetRequiredService<ILedgerVerificationService>().VerifyAllAsync(cancellationToken);
            if (result.IsFailure)
            {
                _logger.LogWarning("Ledger verification skipped: {Message}", result.Error!.Message);
            }
            else
            {
                var report = result.Value;
                _logger.LogInformation(
                    "Ledger verification finished: {Licenses} license(s), {Entries} entries, {Broken} broken",
                    report.LicensesChecked, report.EntriesChecked, report.BrokenLicenses);
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Ledger verification failed to run");
            return null;
        }
    }
}
