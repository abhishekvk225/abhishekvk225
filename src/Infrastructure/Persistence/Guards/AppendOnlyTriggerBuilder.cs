using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NexaVerify.Domain.Common;
using NexaVerify.Infrastructure.Persistence.Rls;

namespace NexaVerify.Infrastructure.Persistence.Guards;

/// <summary>
/// Generates INSTEAD OF UPDATE/DELETE triggers that reject any change to <see cref="IAppendOnly"/> tables (ledgers, audit
/// trail, login history). Together with a DENY for the application principal this makes the history immutable even if
/// application code is wrong or compromised.
/// </summary>
public static class AppendOnlyTriggerBuilder
{
    public sealed record AppendOnlyTable(string Schema, string Table)
    {
        public string TriggerName => $"trg_{Table}_AppendOnly";
    }

    public static IReadOnlyList<AppendOnlyTable> Tables(IModel model) =>
        model.GetEntityTypes()
            .Where(e => e.BaseType is null && !e.IsOwned() && typeof(IAppendOnly).IsAssignableFrom(e.ClrType) && e.GetTableName() is not null)
            .Select(e => new AppendOnlyTable(e.GetSchema() ?? "dbo", e.GetTableName()!))
            .DistinctBy(t => (t.Schema, t.Table))
            .OrderBy(t => t.Schema, StringComparer.Ordinal).ThenBy(t => t.Table, StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<string> Build(IModel model) =>
        Tables(model).Select(t =>
        {
            var q = RowLevelSecurityScriptBuilder.Quote;
            var message = $"{t.Schema}.{t.Table} is append-only: UPDATE and DELETE are not allowed.".Replace("'", "''", StringComparison.Ordinal);
            return $"""
                CREATE OR ALTER TRIGGER {q(t.Schema)}.{q(t.TriggerName)} ON {q(t.Schema)}.{q(t.Table)}
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, N'{message}', 1;
                END
                """;
        }).ToList();
}
