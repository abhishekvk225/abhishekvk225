using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Faces;
using NexaVerify.Application.Persistence;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Background;

public sealed class FaceRetentionOptions
{
    public const string SectionName = "Faces:Retention";

    /// <summary>Turns the hourly sweep off (tests drive it by hand).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Recognition history keeps the image fingerprint and the caller's IP address only this long; after that both are blanked
    /// (the row itself, with outcome and cost, stays for billing and reporting).
    /// </summary>
    [Range(1, 3650)]
    public int HistoryPersonalDataDays { get; set; } = 90;

    [Range(10, 5000)]
    public int BatchSize { get; set; } = 200;

    /// <summary>Safety valve: at most this many batches per client and run, so a huge backlog cannot monopolise a node.</summary>
    [Range(1, 10_000)]
    public int MaxBatchesPerClient { get; set; } = 500;
}

/// <summary>
/// Enforces each client's retention period for biometric data. Face data is strictly tenant-scoped (not even the platform can
/// read it), so the sweep lists clients in platform scope and then erases inside each client's own tenant scope. Every client is
/// drained batch by batch until nothing is due, and recognition-history rows lose their image fingerprint and IP after the
/// configured number of days.
/// </summary>
public sealed class FaceRetentionSweeper : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly byte[] BlankHash = new byte[32];

    private readonly IServiceScopeFactory _scopes;
    private readonly FaceRetentionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<FaceRetentionSweeper> _logger;

    public FaceRetentionSweeper(IServiceScopeFactory scopes, IOptions<FaceRetentionOptions> options, TimeProvider time, ILogger<FaceRetentionSweeper> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

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
        var blanked = 0;
        foreach (var clientId in clients)
        {
            try
            {
                erased += await DrainProfilesAsync(clientId, cancellationToken);
                blanked += await PurgeHistoryAsync(clientId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Face retention failed for client {ClientId}", clientId);
            }
        }

        if (erased > 0 || blanked > 0)
        {
            _logger.LogInformation("Retention erased {Profiles} face profile(s) and blanked personal data on {History} history row(s)", erased, blanked);
        }

        return erased;
    }

    /// <summary>Repeats the per-client processor until a batch comes back short (nothing left that is due).</summary>
    private async Task<int> DrainProfilesAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var total = 0;
        for (var batch = 0; batch < _options.MaxBatchesPerClient; batch++)
        {
            await using var scope = _scopes.CreateAsyncScope();
            using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginTenant(clientId);
            var done = await scope.ServiceProvider.GetRequiredService<IFaceRetentionProcessor>().ProcessAsync(_options.BatchSize, cancellationToken);
            total += done;
            if (done < _options.BatchSize)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>
    /// Blanks the image fingerprint and IP address of history rows older than the personal-data window. Both are personal data that
    /// would otherwise live as long as the billing record; the outcome, score and cost stay.
    /// </summary>
    private async Task<int> PurgeHistoryAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var cutoff = _time.GetUtcNow().UtcDateTime.AddDays(-_options.HistoryPersonalDataDays);
        var total = 0;
        for (var batch = 0; batch < _options.MaxBatchesPerClient; batch++)
        {
            await using var scope = _scopes.CreateAsyncScope();
            using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginTenant(clientId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var done = await db.RecognitionRequests
                .Where(r => r.CreatedAt < cutoff && (r.IpAddress != null || r.InputImageSha256 != BlankHash))
                .OrderBy(r => r.CreatedAt).Take(_options.BatchSize)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IpAddress, (string?)null).SetProperty(r => r.InputImageSha256, BlankHash), cancellationToken);
            total += done;
            if (done < _options.BatchSize)
            {
                break;
            }
        }

        return total;
    }
}
