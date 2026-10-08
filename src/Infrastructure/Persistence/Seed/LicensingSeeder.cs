using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Abstractions;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Infrastructure.Persistence.Seed;

/// <summary>Idempotent: default plans and the platform-default credit costs. Existing rows are never overwritten (operators own them after first seed).</summary>
public sealed class LicensingSeeder
{
    private readonly AppDbContext _db;
    private readonly ITenantScope _scope;
    private readonly TimeProvider _time;

    public LicensingSeeder(AppDbContext db, ITenantScope scope, TimeProvider time)
    {
        _db = db;
        _scope = scope;
        _time = time;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        using var platform = _scope.BeginPlatform("licensing seed");
        var now = _time.GetUtcNow().UtcDateTime;

        if (!await _db.Plans.AnyAsync(cancellationToken))
        {
            var trial = Plan.Create("TRIAL", "Trial", 500, 14);
            trial.RateLimitPerMinute = 30;
            trial.MaxApiKeys = 2;
            trial.MaxUsers = 3;
            var starter = Plan.Create("STARTER", "Starter", 10_000, 365);
            var business = Plan.Create("BUSINESS", "Business", 100_000, 365);
            business.RateLimitPerMinute = 300;
            business.MaxApiKeys = 20;
            business.MaxUsers = 50;
            var enterprise = Plan.Create("ENTERPRISE", "Enterprise", 1_000_000, 365);
            enterprise.RateLimitPerMinute = 1200;
            enterprise.MaxApiKeys = 100;
            enterprise.MaxUsers = 500;
            _db.Plans.AddRange(trial, starter, business, enterprise);
        }

        if (!await _db.CostRules.AnyAsync(r => r.PlanId == null, cancellationToken))
        {
            _db.CostRules.AddRange(
                CostRule.Create(null, MeteredOperation.Detect, 0, ChargePolicy.OnCompleted, now),
                CostRule.Create(null, MeteredOperation.Enroll, 1, ChargePolicy.OnSuccess, now),
                CostRule.Create(null, MeteredOperation.Verify, 1, ChargePolicy.OnCompleted, now),
                CostRule.Create(null, MeteredOperation.Identify, 2, ChargePolicy.OnCompleted, now));
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
