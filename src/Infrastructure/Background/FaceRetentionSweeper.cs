using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Faces;
using NexaVerify.Application.Persistence;

namespace NexaVerify.Infrastructure.Background;

/// <summary>
/// Enforces each client's retention period for biometric data. Face data is strictly tenant-scoped (not even the platform can
/// read it), so the sweep lists clients in platform scope and then erases inside each client's own tenant scope.
/// </summary>
public sealed class FaceRetentionSweeper : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<FaceRetentionSweeper> _logger;

    public FaceRetentionSweeper(IServiceScopeFactory scopes, ILogger<FaceRetentionSweeper> logger)
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
                await SweepOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Face retention sweep failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass over all clients; returns how many profiles were erased. Public for tests.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> clients;
        await using (var scope = _scopes.CreateAsyncScope())
        {
            using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("face retention: list clients");
            clients = await scope.ServiceProvider.GetRequiredService<IClientRepository>().ListIdsAsync(cancellationToken);
        }

        var erased = 0;
        foreach (var clientId in clients)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginTenant(clientId);
                erased += await scope.ServiceProvider.GetRequiredService<IFaceRetentionProcessor>().ProcessAsync(200, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Face retention failed for client {ClientId}", clientId);
            }
        }

        if (erased > 0)
        {
            _logger.LogInformation("Retention erased {Count} face profile(s)", erased);
        }

        return erased;
    }
}
