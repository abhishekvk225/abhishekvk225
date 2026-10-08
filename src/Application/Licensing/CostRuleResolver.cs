using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.Licensing;

public sealed record ResolvedCost(int Credits, ChargePolicy Policy, CostRuleScope Scope);

public interface ICostRuleResolver
{
    /// <summary>Client override → plan rule → platform default → built-in fallback, each evaluated at <paramref name="at"/>.</summary>
    Task<ResolvedCost> ResolveAsync(Guid clientId, Guid? planId, MeteredOperation operation, DateTime at, CancellationToken cancellationToken);
}

public sealed class CostRuleResolver : ICostRuleResolver
{
    /// <summary>Safety net only; the seeded platform rules normally win. Detect is free by default.</summary>
    public static ResolvedCost Fallback(MeteredOperation operation) =>
        new(operation == MeteredOperation.Detect ? 0 : 1, ChargePolicy.OnCompleted, CostRuleScope.PlatformDefault);

    private readonly ICostRuleRepository _rules;

    public CostRuleResolver(ICostRuleRepository rules)
    {
        _rules = rules;
    }

    public async Task<ResolvedCost> ResolveAsync(Guid clientId, Guid? planId, MeteredOperation operation, DateTime at, CancellationToken cancellationToken)
    {
        var client = (await _rules.ListClientRulesAsync(clientId, cancellationToken))
            .Where(r => r.Operation == operation && r.AppliesAt(at))
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefault();
        if (client is not null)
        {
            return new ResolvedCost(client.Credits, client.ChargePolicy, CostRuleScope.Client);
        }

        var platform = (await _rules.ListPlatformRulesAsync(cancellationToken)).Where(r => r.Operation == operation && r.AppliesAt(at)).ToList();
        var plan = planId is null ? null : platform.Where(r => r.PlanId == planId).OrderByDescending(r => r.EffectiveFrom).FirstOrDefault();
        if (plan is not null)
        {
            return new ResolvedCost(plan.Credits, plan.ChargePolicy, CostRuleScope.Plan);
        }

        var def = platform.Where(r => r.PlanId is null).OrderByDescending(r => r.EffectiveFrom).FirstOrDefault();
        return def is null ? Fallback(operation) : new ResolvedCost(def.Credits, def.ChargePolicy, CostRuleScope.PlatformDefault);
    }
}
