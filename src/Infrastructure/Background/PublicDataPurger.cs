using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Public;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Background;

/// <summary>
/// Retention for the anonymous public data: sign-ups that were never verified (a day after the link expired), verified ones (a week,
/// the row only keeps the audit trail's context) and contact requests (<c>Signup:ContactRetentionDays</c>, default 180). Idempotent,
/// so every node may run it; deletes are batched.
/// </summary>
public sealed class PublicDataPurger : BackgroundService
{
    public static readonly TimeSpan ExpiredGrace = TimeSpan.FromDays(1);
    public static readonly TimeSpan ConsumedRetention = TimeSpan.FromDays(7);
    private const int Batch = 1000;

    private readonly IServiceScopeFactory _scopes;
    private readonly SignupOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PublicDataPurger> _logger;

    public PublicDataPurger(IServiceScopeFactory scopes, IOptions<SignupOptions> options, TimeProvider time, ILogger<PublicDataPurger> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.PurgeIntervalMinutes));
            do
            {
                await PurgeOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    /// <summary>One purge pass; never throws. Returns (sign-ups removed, contact requests removed). Public for tests.</summary>
    public async Task<(int Signups, int Contacts)> PurgeOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var expiredBefore = now - ExpiredGrace;
            var consumedBefore = now - ConsumedRetention;
            var contactsBefore = now.AddDays(-_options.ContactRetentionDays);

            await using var scope = _scopes.CreateAsyncScope();
            using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("public data retention");
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var signups = 0;
            int removed;
            do
            {
                removed = await db.PendingSignups
                    .Where(p => (p.ConsumedAt == null && p.ExpiresAt < expiredBefore) || (p.ConsumedAt != null && p.ConsumedAt < consumedBefore))
                    .OrderBy(p => p.Id).Take(Batch)
                    .ExecuteDeleteAsync(cancellationToken);
                signups += removed;
            }
            while (removed == Batch);

            var contacts = 0;
            do
            {
                removed = await db.ContactRequests
                    .Where(c => c.CreatedAt < contactsBefore)
                    .OrderBy(c => c.Id).Take(Batch)
                    .ExecuteDeleteAsync(cancellationToken);
                contacts += removed;
            }
            while (removed == Batch);

            if (signups + contacts > 0)
            {
                _logger.LogInformation("Public data retention removed {Signups} pending sign-up(s) and {Contacts} contact request(s)", signups, contacts);
            }

            return (signups, contacts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Purging public data failed; it will be retried");
            return (0, 0);
        }
    }
}
