using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using NexaVerify.Application.Abstractions;

namespace NexaVerify.Infrastructure.Persistence.Rls;

/// <summary>
/// Applies the generated row-level-security objects to the database. Run after migrations by the migration
/// tool/CI (never at request time). Must run in platform scope so the connection can see the tables it protects.
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
        using var platform = _scope.BeginPlatform();
        foreach (var batch in RowLevelSecurityScriptBuilder.Build(context.GetService<IDesignTimeModel>().Model))
        {
            await context.Database.ExecuteSqlRawAsync(batch, cancellationToken);
        }
    }
}
