using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using NexaVerify.Infrastructure.Persistence.Rls;

namespace NexaVerify.Infrastructure.Persistence.Guards;

/// <summary>
/// Installs every database-level guard derived from the model — row-level security for tenant tables and append-only
/// triggers — in ONE transaction. Run by the migrator after migrations (never at request time).
/// </summary>
public sealed class DatabaseGuardsInstaller
{
    private readonly RowLevelSecurityInstaller _rls;

    public DatabaseGuardsInstaller(RowLevelSecurityInstaller rls)
    {
        _rls = rls;
    }

    public static IReadOnlyList<string> BuildBatches(IModel model) =>
        [.. RowLevelSecurityScriptBuilder.Build(model), .. AppendOnlyTriggerBuilder.Build(model)];

    public Task InstallAsync(DbContext context, CancellationToken cancellationToken = default) =>
        _rls.ApplyAsync(context, BuildBatches(context.GetService<IDesignTimeModel>().Model), cancellationToken);

    /// <summary>The policy is SCHEMABINDING, so it must be dropped before migrations alter protected tables.</summary>
    public async Task DropPolicyAsync(DbContext context, CancellationToken cancellationToken = default) =>
        await context.Database.ExecuteSqlRawAsync(RowLevelSecurityScriptBuilder.DropPolicy(), cancellationToken);
}
