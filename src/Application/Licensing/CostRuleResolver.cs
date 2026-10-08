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
    // The resolver is request-scoped; a preflight and the charge that follows it resolve the same (client, plan, operation).
    private readonly Dictionary<(Guid, Guid?, MeteredOperation), ResolvedCost> _memo = [];

    public CostRuleResolver(ICostRuleRepository rules)
    {
        _rules = rules;
    }

    public async Task<ResolvedCost> ResolveAsync(Guid clientId, Guid? planId, MeteredOperation operation, DateTime at, CancellationToken cancellationToken)
    {
        var key = (clientId, planId, operation);
        if (_memo.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var resolved = await ResolveUncachedAsync(clientId, planId, operation, at, cancellationToken);
        _memo[key] = resolved;
        return resolved;
    }

    private async Task<ResolvedCost> ResolveUncachedAsync(Guid clientId, Guid? planId, MeteredOperation operation, DateTime at, CancellationToken cancellationToken)
    {
        if (await _rules.FindClientRuleAsync(clientId, operation, at, cancellationToken) is { } client)
        {
            return new ResolvedCost(client.Credits, client.ChargePolicy, CostRuleScope.Client);
        }

        var platform = await _rules.FindPlatformRulesAsync(operation, at, cancellationToken);
        var plan = planId is null ? null : platform.FirstOrDefault(r => r.PlanId == planId);
        if (plan is not null)
        {
            return new ResolvedCost(plan.Credits, plan.ChargePolicy, CostRuleScope.Plan);
        }

        var def = platform.FirstOrDefault(r => r.PlanId is null);
        return def is null ? Fallback(operation) : new ResolvedCost(def.Credits, def.ChargePolicy, CostRuleScope.PlatformDefault);
    }
}
