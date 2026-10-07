using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NexaVerify.Domain.Common;

namespace NexaVerify.Infrastructure.Persistence;

/// <summary>
/// Structural rules that keep tenant isolation complete as the model grows. A violation means some table could
/// escape the EF filter and/or the SQL row-level-security policy, so the model refuses to build.
/// </summary>
public static class TenantModelRules
{
    public const string ClientIdProperty = nameof(ITenantOwned.ClientId);

    public static IReadOnlyList<string> Validate(IReadOnlyModel model)
    {
        var violations = new List<string>();

        foreach (var entityType in model.GetEntityTypes())
        {
            var clr = entityType.ClrType;
            var isTenant = typeof(ITenantOwned).IsAssignableFrom(clr);
            var isStrict = typeof(IStrictTenantOwned).IsAssignableFrom(clr);

            if (entityType.IsOwned() && entityType.FindOwnership() is { } ownership
                && typeof(ITenantOwned).IsAssignableFrom(ownership.PrincipalEntityType.GetRootType().ClrType))
            {
                // OwnsMany always gets its own table; OwnsOne shares the owner's table unless mapped elsewhere.
                var ownTable = !ownership.IsUnique
                    || (entityType.GetTableName() is { } t && t != ownership.PrincipalEntityType.GetTableName());
                if (ownTable)
                {
                    violations.Add($"{clr.Name} is an owned type stored in its own table; model tenant child data as a tenant-owned entity instead.");
                }

                continue;
            }

            if (!isTenant)
            {
                if (entityType.FindProperty(ClientIdProperty) is not null)
                {
                    violations.Add($"{clr.Name} has a ClientId property but does not implement ITenantOwned.");
                }

                continue;
            }

            var root = entityType.GetRootType();
            var rootTenant = typeof(ITenantOwned).IsAssignableFrom(root.ClrType);
            if (!rootTenant)
            {
                violations.Add($"{clr.Name} is tenant-owned but its base type {root.ClrType.Name} is not; the base table would be unprotected.");
            }
            else if (typeof(IStrictTenantOwned).IsAssignableFrom(root.ClrType) != isStrict)
            {
                violations.Add($"{clr.Name} must have the same tenant strictness as its base type {root.ClrType.Name}.");
            }

            if (entityType.FindProperty(ClientIdProperty) is null)
            {
                violations.Add($"{clr.Name} implements ITenantOwned but ClientId is not mapped.");
            }

            if (entityType.BaseType is not null && entityType.GetTableName() != root.GetTableName())
            {
                violations.Add($"{clr.Name} is mapped to a different table than its base type {root.ClrType.Name} (TPT/TPC is not supported for tenant data).");
            }

            if (entityType.BaseType is null && !entityType.IsOwned() && entityType.GetTableName() is null)
            {
                violations.Add($"{clr.Name} is tenant-owned but not mapped to a table (views/keyless are not supported).");
            }
        }

        return violations;
    }

    public static void EnsureValid(IReadOnlyModel model)
    {
        var violations = Validate(model);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException("Tenant isolation model rules violated:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
        }
    }
}
