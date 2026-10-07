using System.Data.Common;
using System.Runtime.CompilerServices;
using NexaVerify.Application.Abstractions;

namespace NexaVerify.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Writes the current tenant into SQL Server <c>SESSION_CONTEXT</c> (read by the row-level-security policy).
/// It is applied when a connection opens (pool-safe) <b>and</b> re-applied before any command whenever the tenant
/// scope has changed since — so a scope switch inside an open transaction or a long-lived connection cannot leave
/// the previous tenant active.
/// </summary>
public sealed class TenantSessionContextApplier
{
    public const string ClientIdKey = "ClientId";
    public const string IsPlatformKey = "IsPlatform";

    private static readonly ConditionalWeakTable<DbConnection, Applied> State = new();

    private readonly ITenantContext _tenant;

    public TenantSessionContextApplier(ITenantContext tenant)
    {
        _tenant = tenant;
    }

    public void ForceApply(DbConnection connection, DbTransaction? transaction = null)
    {
        using var command = CreateCommand(connection, transaction);
        command.ExecuteNonQuery();
        Remember(connection);
    }

    public async Task ForceApplyAsync(DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        Remember(connection);
    }

    public bool IsCurrent(DbConnection connection) =>
        State.TryGetValue(connection, out var applied) && applied.ClientId == _tenant.ClientId && applied.IsPlatform == _tenant.IsPlatform;

    public void ApplyIfChanged(DbConnection connection, DbTransaction? transaction)
    {
        if (!IsCurrent(connection))
        {
            ForceApply(connection, transaction);
        }
    }

    public Task ApplyIfChangedAsync(DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken) =>
        IsCurrent(connection) ? Task.CompletedTask : ForceApplyAsync(connection, transaction, cancellationToken);

    private void Remember(DbConnection connection)
    {
        State.Remove(connection);
        State.Add(connection, new Applied(_tenant.ClientId, _tenant.IsPlatform));
    }

    private DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"EXEC sp_set_session_context @key = N'{ClientIdKey}', @value = @clientId, @read_only = 0; " +
            $"EXEC sp_set_session_context @key = N'{IsPlatformKey}', @value = @isPlatform, @read_only = 0;";

        var clientId = command.CreateParameter();
        clientId.ParameterName = "@clientId";
        clientId.DbType = System.Data.DbType.Guid;
        clientId.Value = _tenant.ClientId is { } id ? id : DBNull.Value;
        command.Parameters.Add(clientId);

        var isPlatform = command.CreateParameter();
        isPlatform.ParameterName = "@isPlatform";
        isPlatform.Value = _tenant.IsPlatform ? 1 : 0;
        command.Parameters.Add(isPlatform);

        return command;
    }

    private sealed record Applied(Guid? ClientId, bool IsPlatform);
}
