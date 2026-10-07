using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using NexaVerify.Application.Abstractions;

namespace NexaVerify.Infrastructure.Persistence.Rls;

/// <summary>
/// Applies the generated row-level-security objects. Everything runs in ONE transaction so the tables are never left
/// without a policy (a failure rolls back to the previous, still-protecting policy). Run by the migrator/CI — never at
/// request time — in platform scope so the connection can see the tables it protects.
/// </summary>
public sealed class RowLevelSecurityInstaller
{
    private readonly ITenantScope _scope;

    public RowLevelSecurityInstaller(ITenantScope scope)
    {
        _scope = scope;
    }

    public async Task InstallAsync(DbContext context, CancellationToken cancellationToken = default)
    {
        using var platform = _scope.BeginPlatform("install row-level security");
        var batches = RowLevelSecurityScriptBuilder.Build(context.GetService<IDesignTimeModel>().Model);
        await ApplyAsync(context, batches, cancellationToken);
    }

    /// <summary>Runs the batches atomically; a failure leaves the previous policy in place.</summary>
    public async Task ApplyAsync(DbContext context, IReadOnlyList<string> batches, CancellationToken cancellationToken = default)
    {
        using var platform = _scope.BeginPlatform("apply row-level security batches");
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        foreach (var batch in batches)
        {
            await context.Database.ExecuteSqlRawAsync(batch, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
