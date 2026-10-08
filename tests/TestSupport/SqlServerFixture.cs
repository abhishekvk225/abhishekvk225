using Xunit;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace NexaVerify.TestSupport;

/// <summary>
/// One SQL Server 2022 container per test run, shared through <see cref="SqlServerCollection"/>.
/// If TEST_SQL_CONNECTION is set, that server is used instead of starting a container
/// (needed where Docker-in-Docker is unavailable).
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;
    private string _adminConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("TEST_SQL_CONNECTION");
        if (!string.IsNullOrWhiteSpace(external))
        {
            _adminConnectionString = external;
            return;
        }

        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU16-ubuntu-22.04").Build();
        await _container.StartAsync();
        _adminConnectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Creates an empty, uniquely named database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = "nv_" + Guid.NewGuid().ToString("N");
        await using (var connection = new SqlConnection(_adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{name}]";
            await command.ExecuteNonQueryAsync();
        }

        return new SqlConnectionStringBuilder(_adminConnectionString) { InitialCatalog = name }.ConnectionString;
    }

    /// <summary>
    /// Drops a database created by <see cref="CreateDatabaseAsync"/> (best effort). Every test class gets its own database and each costs
    /// tens of megabytes, so a long run would otherwise fill the container's disk.
    /// </summary>
    public static async Task TryDropDatabaseAsync(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var name = builder.InitialCatalog;
            if (!name.StartsWith("nv_", StringComparison.Ordinal))
            {
                return;
            }

            builder.InitialCatalog = "master";
            builder.Pooling = false;
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // cleanup only: never fail a test because the database could not be dropped
        }
    }

    /// <summary>A connection string that can never reach a server (for readiness-failure tests).</summary>
    public static string UnreachableConnectionString =>
        "Server=127.0.0.1,1;Database=none;User Id=x;Password=x;Encrypt=False;Connect Timeout=2";
}

public static class SqlServerCollection
{
    /// <summary>Name of the shared-container collection. Each test assembly declares its own definition (xUnit requires it).</summary>
    public const string Name = "SqlServer";
}
