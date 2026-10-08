using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Licensing;

namespace NexaVerify.Infrastructure.Background;

/// <summary>
/// Finishes an on-demand ledger verification after the HTTP request that started it has returned. The worker owns the one-at-a-time
/// lease (in-process gate + cluster lock) from the moment it is handed over and always releases it, even if the scan fails.
/// It runs until it completes or the host stops.
/// </summary>
public sealed class LedgerRunLauncher : ILedgerRunLauncher
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<LedgerRunLauncher> _logger;

    public LedgerRunLauncher(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ILogger<LedgerRunLauncher> logger)
    {
        _scopes = scopes;
        _lifetime = lifetime;
        _logger = logger;
    }

    public void Launch(Guid runId, Guid? licenseId, IAsyncDisposable lease) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("ledger verification (on demand)");
                await scope.ServiceProvider.GetRequiredService<ILedgerVerificationService>().ExecuteRunAsync(runId, licenseId, _lifetime.ApplicationStopping);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ledger verification run {RunId} ended with an error", runId);
            }
            finally
            {
                await lease.DisposeAsync();
            }
        });
}
