using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NexaVerify.Application.Abstractions;

namespace NexaVerify.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Pushes the current tenant into SQL Server <c>SESSION_CONTEXT</c> every time a connection is opened
/// (pool-safe — a pooled connection never keeps a previous request's tenant). The row-level-security
/// policy reads these keys, so even raw SQL or a forgotten EF filter cannot cross tenants.
/// </summary>
public sealed class TenantSessionContextInterceptor : DbConnectionInterceptor
{
    public const string ClientIdKey = "ClientId";
    public const string IsPlatformKey = "IsPlatform";

    private readonly ITenantContext _tenant;

    public TenantSessionContextInterceptor(ITenantContext tenant)
    {
        _tenant = tenant;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
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
}
