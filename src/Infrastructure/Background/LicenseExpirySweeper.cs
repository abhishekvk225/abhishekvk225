using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Licensing;

namespace NexaVerify.Infrastructure.Background;

/// <summary>
/// Housekeeping only: whether a license is usable is always decided from its timestamps at the moment of use, so a late or
/// missed sweep can never let an expired license be charged. The sweeper flips the status and writes the unused credits off.
/// </summary>
public sealed class LicenseExpirySweeper : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<LicenseExpirySweeper> _logger;

    public LicenseExpirySweeper(IServiceScopeFactory scopes, ILogger<LicenseExpirySweeper> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("license expiry sweep");
                var count = await scope.ServiceProvider.GetRequiredService<ILicenseExpiryProcessor>().ProcessAsync(100, stoppingToken);
                if (count > 0)
                {
                    _logger.LogInformation("Expired {Count} license(s)", count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "License expiry sweep failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
