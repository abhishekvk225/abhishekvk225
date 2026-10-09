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
            var trial = NewTrialPlan();
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

        await _db.SaveChangesAsync(cancellationToken);

        // The public website needs a trial plan to point sign-ups at; deployments that predate it (or lost it) get one. The migration
        // flags an existing TRIAL plan, so this never duplicates it.
        if (!await _db.Plans.AnyAsync(p => p.IsTrial, cancellationToken) && !await _db.Plans.AnyAsync(p => p.Code == "TRIAL", cancellationToken))
        {
            _db.Plans.Add(NewTrialPlan());
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

    /// <summary>The public "Free trial": its public credits and period are the <c>Signup:TrialCredits</c> / <c>Signup:TrialDays</c> values.</summary>
    private static Plan NewTrialPlan()
    {
        var trial = Plan.Create("TRIAL", "Free trial", 100, 14);
        trial.Description = "Try NexaVerify with your own data. No credit card required.";
        trial.RateLimitPerMinute = 30;
        trial.MaxApiKeys = 2;
        trial.MaxUsers = 3;
        trial.IsPublic = true;
        trial.IsTrial = true;
        trial.DisplayOrder = 0;
        trial.Highlights = ["Full API and web portal access", "Register, verify and identify faces", "No credit card required"];
        return trial;
    }
}
