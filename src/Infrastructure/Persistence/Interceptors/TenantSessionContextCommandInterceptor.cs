using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NexaVerify.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Re-applies the tenant before a command when the scope changed since the connection was opened
/// (e.g. inside a transaction), closing the gap that connection-open-only application would leave.
/// </summary>
public sealed class TenantSessionContextCommandInterceptor : DbCommandInterceptor
{
    private readonly TenantSessionContextApplier _applier;

    public TenantSessionContextCommandInterceptor(TenantSessionContextApplier applier)
    {
        _applier = applier;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Ensure(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(command, cancellationToken);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Ensure(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(command, cancellationToken);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Ensure(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(command, cancellationToken);
        return result;
    }

    private void Ensure(DbCommand command)
    {
        if (command.Connection is { } connection)
        {
            _applier.ApplyIfChanged(connection, command.Transaction);
        }
    }

    private Task EnsureAsync(DbCommand command, CancellationToken cancellationToken) =>
        command.Connection is { } connection
            ? _applier.ApplyIfChangedAsync(connection, command.Transaction, cancellationToken)
            : Task.CompletedTask;
}
