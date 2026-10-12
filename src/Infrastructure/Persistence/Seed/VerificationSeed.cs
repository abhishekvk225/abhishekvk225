using Microsoft.EntityFrameworkCore;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Infrastructure.Persistence.Seed;

/// <summary>
/// Idempotent M14 defaults, run by <see cref="LicensingSeeder"/> (platform scope): one platform-default cost rule per verification operation
/// (<see cref="VerificationOperations.DefaultCredits"/>) and an enabled switch per Available module. Existing rows are never overwritten:
/// an operator who changed a price or switched a module off keeps that decision.
/// </summary>
internal static class VerificationSeed
{
    public static async Task EnsureAsync(AppDbContext db, DateTime now, CancellationToken cancellationToken)
    {
        foreach (var (operation, credits) in VerificationOperations.DefaultCredits)
        {
            if (!await db.CostRules.AnyAsync(r => r.PlanId == null && r.Operation == operation, cancellationToken))
            {
                db.CostRules.Add(CostRule.Create(null, operation, credits, VerificationOperations.DefaultPolicy, now));
            }
        }

        foreach (var key in VerificationModuleKeys.Available)
        {
            if (!await db.VerificationModuleSettings.AnyAsync(s => s.ModuleKey == key, cancellationToken))
            {
                db.VerificationModuleSettings.Add(VerificationModuleSetting.Create(key, true, now));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
