using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NexaVerify.Infrastructure.Persistence.Rls;

namespace NexaVerify.Infrastructure.Persistence.Guards;

/// <summary>
/// Readiness check: every tenant-owned table in the model must be covered by the enabled RLS policy and every append-only
/// table by its trigger. An unprotected table (a migration that was not followed by the guard installer) makes the
/// instance unready, so it never serves traffic fail-open.
/// </summary>
public sealed class TenantProtectionHealthCheck : IHealthCheck
{
    private readonly AppDbContext _db;
    private readonly ILogger<TenantProtectionHealthCheck> _logger;

    public TenantProtectionHealthCheck(AppDbContext db, ILogger<TenantProtectionHealthCheck> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var model = _db.GetService<IDesignTimeModel>().Model;
        var expectedRls = RowLevelSecurityScriptBuilder.TenantTables(model).Select(t => $"{t.Schema}.{t.Table}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedTriggers = AppendOnlyTriggerBuilder.Tables(model).Select(t => $"{t.Schema}.{t.TriggerName}").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var protectedTables = (await _db.Database.SqlQueryRaw<string>(
                """
                SELECT DISTINCT s.name + N'.' + t.name AS [Value]
                FROM sys.security_predicates sp
                JOIN sys.security_policies pol ON pol.object_id = sp.object_id
                JOIN sys.tables t ON t.object_id = sp.target_object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE pol.name = N'TenantPolicy' AND pol.is_enabled = 1 AND sp.predicate_type_desc = N'FILTER'
                """).ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var triggers = (await _db.Database.SqlQueryRaw<string>(
                """
                SELECT s.name + N'.' + tr.name AS [Value]
                FROM sys.triggers tr
                JOIN sys.tables t ON t.object_id = tr.parent_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE tr.is_disabled = 0
                """).ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingRls = expectedRls.Except(protectedTables).ToList();
        var missingTriggers = expectedTriggers.Except(triggers).ToList();
        if (missingRls.Count == 0 && missingTriggers.Count == 0)
        {
            return HealthCheckResult.Healthy();
        }

        _logger.LogCritical(
            "Database guards incomplete. Missing RLS: [{Rls}]. Missing append-only triggers: [{Triggers}]. Run the migrator.",
            string.Join(", ", missingRls), string.Join(", ", missingTriggers));
        return HealthCheckResult.Unhealthy("Database guards are incomplete.");
    }
}
