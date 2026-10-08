using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Application.Licensing;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Platform;

/// <summary>
/// Cross-node mutual exclusion through a SQL Server session application lock (<c>sp_getapplock</c>) held on a dedicated connection.
/// The lock vanishes with the connection, so a crashed node can never leave it stuck.
/// </summary>
public sealed class SqlDistributedLock : IDistributedLock
{
    private readonly IServiceScopeFactory _scopes;

    public SqlDistributedLock(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken)
    {
        string connectionString;
        await using (var scope = _scopes.CreateAsyncScope())
        {
            connectionString = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString()
                ?? throw new InvalidOperationException("No database connection string is configured.");
        }

        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "EXEC @result = sp_getapplock @Resource = @name, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0";
            command.Parameters.AddWithValue("@name", "nexaverify:" + name);
            var result = command.Parameters.Add("@result", SqlDbType.Int);
            result.Direction = ParameterDirection.Output;
            await command.ExecuteNonQueryAsync(cancellationToken);
            if ((int)result.Value >= 0)
            {
                return new Handle(connection, name);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return null;
    }

    private sealed class Handle : IAsyncDisposable
    {
        private readonly SqlConnection _connection;
        private readonly string _name;

        public Handle(SqlConnection connection, string name)
        {
            _connection = connection;
            _name = name;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = _connection.CreateCommand();
                command.CommandText = "EXEC sp_releaseapplock @Resource = @name, @LockOwner = 'Session'";
                command.Parameters.AddWithValue("@name", "nexaverify:" + _name);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch
            {
                // closing the connection releases the lock regardless
            }
            finally
            {
                await _connection.DisposeAsync();
            }
        }
    }
}
