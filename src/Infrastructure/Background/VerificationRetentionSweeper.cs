using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Persistence;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Processing;

namespace NexaVerify.Infrastructure.Background;

public sealed class VerificationRetentionJobOptions
{
    public const string SectionName = "Verification:RetentionJob";

    /// <summary>Turns the sweep off (tests drive it by hand).</summary>
    public bool Enabled { get; set; } = true;

    [Range(1, 1440)]
    public int IntervalMinutes { get; set; } = 60;

    [Range(10, 5000)]
    public int BatchSize { get; set; } = 200;

    /// <summary>Safety valve: at most this many batches per client and run, so a huge backlog cannot monopolise a node.</summary>
    [Range(1, 10_000)]
    public int MaxBatchesPerClient { get; set; } = 500;

    /// <summary>Download grants are useless after their (60 s) lifetime; they are removed this long after they expired.</summary>
    [Range(1, 168)]
    public int GrantRetentionHours { get; set; } = 24;
}

/// <summary>
/// Enforces the retention windows of docs/07 §9.4. Documents, results and checks are strict tenant tables, so the sweep lists clients in
/// platform scope and then deletes inside each client's own tenant scope: documents past <c>PurgeAfter</c>, results (with their checks) past
/// <c>RetainUntil</c>, and whole requests past <c>ExpiresAt</c> (the latest purge time of the request). Personal metadata (the caller's IP) is
/// blanked after <c>PersonalMetadataDays</c> and expired download grants are removed (both in platform scope: tenant-or-platform tables).
/// Request metadata and its append-only events are kept for billing and audit (<c>MetadataDays</c>: no purge is implemented, see docs/07 §18).
/// </summary>
public sealed class VerificationRetentionSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly VerificationRetentionJobOptions _job;
    private readonly VerificationOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<VerificationRetentionSweeper> _logger;

    public VerificationRetentionSweeper(
        IServiceScopeFactory scopes, IOptions<VerificationRetentionJobOptions> job, IOptions<VerificationOptions> options, TimeProvider time,
        ILogger<VerificationRetentionSweeper> logger)
    {
        _scopes = scopes;
        _job = job.Value;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_job.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_job.IntervalMinutes));
        try
        {
            do
            {
                try
                {
                    await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError("Verification retention sweep failed ({ExceptionType})", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    public sealed record SweepResult(int Documents, int Results, int Requests, int Grants, int IpAddresses);

    /// <summary>One pass over all clients. Public for tests.</summary>
    public async Task<SweepResult> SweepOnceAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        IReadOnlyList<Guid> clients;
        var grants = 0;
        var blanked = 0;
        await using (var scope = _scopes.CreateAsyncScope())
        {
            using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("verification retention: list clients, grants, ip");
            clients = await scope.ServiceProvider.GetRequiredService<IClientRepository>().ListIdsAsync(cancellationToken).ConfigureAwait(false);
            var retention = scope.ServiceProvider.GetRequiredService<IVerificationRetention>();
            grants = await DrainAsync(take => retention.PurgeExpiredGrantsAsync(now.AddHours(-_job.GrantRetentionHours), take, cancellationToken), cancellationToken).ConfigureAwait(false);
            blanked = await DrainAsync(take => retention.BlankIpAddressesAsync(now.AddDays(-_options.Retention.PersonalMetadataDays), take, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        int documents = 0, results = 0, requests = 0;
        foreach (var clientId in clients)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginTenant(clientId);
                var retention = scope.ServiceProvider.GetRequiredService<IVerificationRetention>();

                documents += await DrainIdsAsync(take => retention.ListDocumentsDueAsync(now, take, cancellationToken), id => retention.PurgeDocumentAsync(id, now, cancellationToken), cancellationToken).ConfigureAwait(false);
                results += await DrainIdsAsync(take => retention.ListResultsDueAsync(now, take, cancellationToken), id => retention.PurgeResultAsync(id, cancellationToken), cancellationToken).ConfigureAwait(false);
                requests += await DrainIdsAsync(
                    async take => (await retention.ListRequestsDueAsync(now, take, cancellationToken).ConfigureAwait(false)).Select(d => d.RequestId).ToList(),
                    id => retention.EraseAsync(id, now, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError("Verification retention failed for client {ClientId} ({ExceptionType})", clientId, ex.GetType().Name);
            }
        }

        if (documents + results + requests + grants + blanked > 0)
        {
            _logger.LogInformation(
                "Verification retention purged {Documents} document(s), {Results} result(s), erased {Requests} request(s), removed {Grants} grant(s), blanked {Ips} address(es)",
                documents, results, requests, grants, blanked);
        }

        return new SweepResult(documents, results, requests, grants, blanked);
    }

    private async Task<int> DrainAsync(Func<int, Task<int>> step, CancellationToken cancellationToken)
    {
        var total = 0;
        for (var batch = 0; batch < _job.MaxBatchesPerClient; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var done = await step(_job.BatchSize).ConfigureAwait(false);
            total += done;
            if (done < _job.BatchSize)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>Lists due ids and purges them one by one until a batch comes back short.</summary>
    private async Task<int> DrainIdsAsync(Func<int, Task<IReadOnlyList<Guid>>> list, Func<Guid, Task<bool>> purge, CancellationToken cancellationToken)
    {
        var total = 0;
        for (var batch = 0; batch < _job.MaxBatchesPerClient; batch++)
        {
            var ids = await list(_job.BatchSize).ConfigureAwait(false);
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await purge(id).ConfigureAwait(false))
                {
                    total++;
                }
            }

            if (ids.Count < _job.BatchSize)
            {
                break;
            }
        }

        return total;
    }
}
