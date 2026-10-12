using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Processing;

namespace NexaVerify.Infrastructure.Background;

/// <summary>
/// Processes queued verifications (docs/07 §7.4). Any number of nodes can run it. Each cycle it (1) recovers: leases that expired go
/// back to the queue until the attempt cap, stale queued requests expire (neither is ever charged), (2) claims due requests in
/// PLATFORM scope with one set-based statement (the claim touches the personal-data-free request table only) and (3) processes each claim
/// in a fresh DI scope bound to THAT request's tenant, with a heartbeat that extends the lease while it works. Documents, results and
/// checks are strict tenant tables, so they are unreachable without the tenant scope. On shutdown everything still held is handed back
/// without counting the attempt.
/// </summary>
public sealed class VerificationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly VerificationOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<VerificationWorker> _logger;
    private readonly Guid _leaseToken = Guid.NewGuid();

    public VerificationWorker(IServiceScopeFactory scopes, IOptions<VerificationOptions> options, TimeProvider time, ILogger<VerificationWorker> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Worker.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.Worker.PollSeconds));
        try
        {
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError("Verification worker cycle failed ({ExceptionType})", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            await HandBackAsync().ConfigureAwait(false);
        }
    }

    /// <summary>One housekeeping + claim + process cycle; returns how many requests were processed. Public so tests drive the worker by hand.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await RecoverAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ClaimedVerification> claims;
        await using (var scope = _scopes.CreateAsyncScope())
        {
            using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("verification worker: claim");
            var now = _time.GetUtcNow().UtcDateTime;
            claims = await scope.ServiceProvider.GetRequiredService<IVerificationQueue>().ClaimDueAsync(
                _options.Worker.BatchSize, _options.Worker.PerClientLimit, now, TimeSpan.FromSeconds(_options.Worker.LeaseSeconds), _leaseToken, cancellationToken).ConfigureAwait(false);
        }

        if (claims.Count == 0)
        {
            return 0;
        }

        await Parallel.ForEachAsync(
            claims,
            new ParallelOptions { MaxDegreeOfParallelism = _options.Worker.Parallelism, CancellationToken = cancellationToken },
            async (claim, ct) => await ProcessClaimAsync(claim, ct).ConfigureAwait(false)).ConfigureAwait(false);
        return claims.Count;
    }

    /// <summary>Expired leases (a crashed node) and stale queued requests. Neither consumes credits.</summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("verification worker: recover");
        var queue = scope.ServiceProvider.GetRequiredService<IVerificationQueue>();
        var now = _time.GetUtcNow().UtcDateTime;
        var swept = await queue.RequeueExpiredLeasesAsync(now, _options.Processing.MaxAttempts, _options.Worker.HousekeepingBatch, cancellationToken).ConfigureAwait(false);
        var expired = await queue.ExpireStaleQueuedAsync(now.AddMinutes(-_options.Processing.MaxQueueAgeMinutes), now, _options.Worker.HousekeepingBatch, cancellationToken).ConfigureAwait(false);
        if (swept.Requeued + swept.Failed + expired > 0)
        {
            _logger.LogWarning("Verification housekeeping: {Requeued} lease(s) recovered, {Failed} failed after the attempt cap, {Expired} expired", swept.Requeued, swept.Failed, expired);
        }
    }

    private async Task ProcessClaimAsync(ClaimedVerification claim, CancellationToken cancellationToken)
    {
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = HeartbeatAsync(claim, work, heartbeatStop.Token);
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginTenant(claim.ClientId);
            await scope.ServiceProvider.GetRequiredService<IVerificationProcessor>().ProcessAsync(
                new ClaimedRequest(claim.RequestId, claim.ClientId, claim.Type, claim.LeaseToken, claim.Attempts), work.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // shutdown: HandBackAsync gives the request back
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Verification {RequestId} lost its lease while processing; another node owns it now", claim.RequestId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The processor handles expected faults itself; this is the last line. Type and id only: a message may quote document text.
            _logger.LogError("Verification {RequestId} aborted ({ExceptionType}); the lease will expire and the sweeper decides", claim.RequestId, ex.GetType().Name);
        }
        finally
        {
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }
    }

    /// <summary>Extends the lease every third of its length. When the lease is lost the processing token is cancelled.</summary>
    private async Task HeartbeatAsync(ClaimedVerification claim, CancellationTokenSource work, CancellationToken stop)
    {
        var lease = TimeSpan.FromSeconds(_options.Worker.LeaseSeconds);
        using var timer = new PeriodicTimer(lease / 3);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                await using var scope = _scopes.CreateAsyncScope();
                using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("verification worker: heartbeat");
                var now = _time.GetUtcNow().UtcDateTime;
                var extended = await scope.ServiceProvider.GetRequiredService<IVerificationQueue>()
                    .ExtendLeaseAsync(claim.RequestId, claim.LeaseToken, now + lease, now, stop).ConfigureAwait(false);
                if (!extended)
                {
                    await work.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // processing finished or the host is stopping
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("Lease heartbeat for verification {RequestId} failed ({ExceptionType})", claim.RequestId, ex.GetType().Name);
        }
    }

    private async Task HandBackAsync()
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("verification worker: shutdown hand-back");
            var now = _time.GetUtcNow().UtcDateTime;
            var released = await scope.ServiceProvider.GetRequiredService<IVerificationQueue>().ReleaseHeldAsync(_leaseToken, now, now, CancellationToken.None).ConfigureAwait(false);
            if (released > 0)
            {
                _logger.LogInformation("Verification worker handed back {Count} request(s) on shutdown", released);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("Could not hand back held verifications on shutdown ({ExceptionType}); the leases will expire", ex.GetType().Name);
        }
    }
}
