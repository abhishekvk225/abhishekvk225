using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NexaVerify.Infrastructure.Persistence.Interceptors;

/// <summary>Applies the tenant to the session whenever a connection is opened (including from the pool).</summary>
public sealed class TenantSessionContextInterceptor : DbConnectionInterceptor
{
    private readonly TenantSessionContextApplier _applier;

    public TenantSessionContextInterceptor(TenantSessionContextApplier applier)
    {
        _applier = applier;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        _applier.ForceApply(connection);

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default) =>
        _applier.ForceApplyAsync(connection, null, cancellationToken);
}
