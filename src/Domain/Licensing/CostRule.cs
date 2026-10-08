using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Licensing;

/// <summary>
/// "This operation costs N credits and is charged under this policy" — platform default (no plan), per plan, and (see
/// <see cref="ClientCostRule"/>) per client. Rules are versioned by <see cref="EffectiveFrom"/>: they are closed and replaced,
/// never edited, so historical charges stay explainable.
/// </summary>
public sealed class CostRule : AuditableEntity
{
    private CostRule()
    {
    }

    public Guid? PlanId { get; private set; }

    public MeteredOperation Operation { get; private set; }

    public int Credits { get; private set; }

    public ChargePolicy ChargePolicy { get; private set; }

    public DateTime EffectiveFrom { get; private set; }

    public DateTime? EffectiveTo { get; private set; }

    public static CostRule Create(Guid? planId, MeteredOperation operation, int credits, ChargePolicy policy, DateTime effectiveFrom)
    {
        if (credits is < 0 or > 1000)
        {
            throw new DomainException("COST_INVALID", "A cost must be between 0 and 1000 credits.");
        }

        return new CostRule { PlanId = planId, Operation = operation, Credits = credits, ChargePolicy = policy, EffectiveFrom = effectiveFrom };
    }

    public bool AppliesAt(DateTime at) => IsActive && EffectiveFrom <= at && (EffectiveTo is null || at < EffectiveTo);

    public void CloseAt(DateTime at)
    {
        EffectiveTo = at;
    }
}

/// <summary>A client-specific override of an operation's cost (tenant-owned).</summary>
public sealed class ClientCostRule : AuditableEntity, ITenantOwned
{
    private ClientCostRule()
    {
    }

    public Guid ClientId { get; set; }

    public MeteredOperation Operation { get; private set; }

    public int Credits { get; private set; }

    public ChargePolicy ChargePolicy { get; private set; }

    public DateTime EffectiveFrom { get; private set; }

    public DateTime? EffectiveTo { get; private set; }

    public static ClientCostRule Create(Guid clientId, MeteredOperation operation, int credits, ChargePolicy policy, DateTime effectiveFrom)
    {
        if (credits is < 0 or > 1000)
        {
            throw new DomainException("COST_INVALID", "A cost must be between 0 and 1000 credits.");
        }

        return new ClientCostRule { ClientId = clientId, Operation = operation, Credits = credits, ChargePolicy = policy, EffectiveFrom = effectiveFrom };
    }

    public bool AppliesAt(DateTime at) => IsActive && EffectiveFrom <= at && (EffectiveTo is null || at < EffectiveTo);

    public void CloseAt(DateTime at)
    {
        EffectiveTo = at;
    }
}

public static class ChargePolicyRules
{
    /// <summary>Whether an operation with this outcome is billable under the policy.</summary>
    public static bool ShouldCharge(this ChargePolicy policy, MeterOutcome outcome) => policy switch
    {
        ChargePolicy.OnAttempt => true,
        ChargePolicy.OnSuccess => outcome == MeterOutcome.Success,
        _ => outcome is MeterOutcome.Success or MeterOutcome.NoMatch,
    };
}
