using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NexaVerify.Domain.Common;

namespace NexaVerify.Infrastructure.Persistence.Rls;

/// <summary>
/// Generates the SQL Server row-level-security objects for every tenant-owned table in the EF model, so a new
/// tenant-owned entity is protected automatically. The output is a list of batches that must run in one transaction
/// and is idempotent (the policy is dropped and recreated). Predicates fail closed when SESSION_CONTEXT is not set.
/// </summary>
public static class RowLevelSecurityScriptBuilder
{
    public const string SecuritySchema = "security";
    public const string PolicyName = "TenantPolicy";
    public const string TenantFilterFunction = "fn_TenantFilter";
    public const string StrictTenantFilterFunction = "fn_StrictTenantFilter";

    public static IReadOnlyList<TenantTable> TenantTables(IModel model)
    {
        TenantModelRules.EnsureValid(model);

        return model.GetEntityTypes()
            .Where(e => e.BaseType is null && !e.IsOwned() && typeof(ITenantOwned).IsAssignableFrom(e.ClrType))
            .Select(e =>
            {
                var table = e.GetTableName()!;
                var schema = e.GetSchema() ?? "dbo";
                var column = e.FindProperty(TenantModelRules.ClientIdProperty)!
                    .GetColumnName(StoreObjectIdentifier.Table(table, schema)) ?? TenantModelRules.ClientIdProperty;
                return new TenantTable(schema, table, column, typeof(IStrictTenantOwned).IsAssignableFrom(e.ClrType));
            })
            .DistinctBy(t => (t.Schema, t.Table)) // table splitting: several entities, one table
            .OrderBy(t => t.Schema, StringComparer.Ordinal)
            .ThenBy(t => t.Table, StringComparer.Ordinal)
            .ToList();
    }

    public static IReadOnlyList<string> Build(IModel model)
    {
        var tables = TenantTables(model);

        var batches = new List<string>
        {
            $"IF SCHEMA_ID(N'{SecuritySchema}') IS NULL EXEC(N'CREATE SCHEMA [{SecuritySchema}]');",
            DropPolicy(),
            FilterFunction(TenantFilterFunction, allowPlatform: true),
            FilterFunction(StrictTenantFilterFunction, allowPlatform: false),
        };

        if (tables.Count > 0)
        {
            batches.Add(Policy(tables));
        }

        return batches;
    }

    public static string DropPolicy() =>
        $"IF EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'{PolicyName}' AND schema_id = SCHEMA_ID(N'{SecuritySchema}')) " +
        $"DROP SECURITY POLICY [{SecuritySchema}].[{PolicyName}];";

    public static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string FilterFunction(string name, bool allowPlatform)
    {
        var platformClause = allowPlatform
            ? $"CAST(SESSION_CONTEXT(N'{TenantSessionContextApplierKeys.IsPlatform}') AS int) = 1 OR "
            : string.Empty;

        // CREATE OR ALTER is safe here because the policy referencing the function was dropped first (same transaction).
        return $"""
            CREATE OR ALTER FUNCTION [{SecuritySchema}].[{name}](@ClientId uniqueidentifier)
            RETURNS TABLE
            WITH SCHEMABINDING
            AS RETURN
                SELECT 1 AS [allowed]
                WHERE {platformClause}@ClientId = CAST(SESSION_CONTEXT(N'{TenantSessionContextApplierKeys.ClientId}') AS uniqueidentifier);
            """;
    }

    private static string Policy(IReadOnlyList<TenantTable> tables)
    {
        var sb = new StringBuilder();
        sb.Append($"CREATE SECURITY POLICY [{SecuritySchema}].[{PolicyName}]");

        for (var i = 0; i < tables.Count; i++)
        {
            var t = tables[i];
            var function = t.Strict ? StrictTenantFilterFunction : TenantFilterFunction;
            var target = $"{Quote(t.Schema)}.{Quote(t.Table)}";
            var predicate = $"[{SecuritySchema}].[{function}]({Quote(t.ClientIdColumn)})";

            sb.AppendLine();
            sb.Append($"  ADD FILTER PREDICATE {predicate} ON {target},");
            sb.AppendLine();
            sb.Append($"  ADD BLOCK PREDICATE {predicate} ON {target} AFTER INSERT,");
            sb.AppendLine();
            sb.Append($"  ADD BLOCK PREDICATE {predicate} ON {target} AFTER UPDATE,");
            sb.AppendLine();
            sb.Append($"  ADD BLOCK PREDICATE {predicate} ON {target} BEFORE UPDATE,");
            sb.AppendLine();
            sb.Append($"  ADD BLOCK PREDICATE {predicate} ON {target} BEFORE DELETE{(i < tables.Count - 1 ? "," : string.Empty)}");
        }

        sb.AppendLine();
        sb.Append("WITH (STATE = ON, SCHEMABINDING = ON);");
        return sb.ToString();
    }

    public sealed record TenantTable(string Schema, string Table, string ClientIdColumn, bool Strict);
}

/// <summary>SESSION_CONTEXT keys shared by the applier (writer) and the policy functions (reader).</summary>
public static class TenantSessionContextApplierKeys
{
    public const string ClientId = "ClientId";
    public const string IsPlatform = "IsPlatform";
}
