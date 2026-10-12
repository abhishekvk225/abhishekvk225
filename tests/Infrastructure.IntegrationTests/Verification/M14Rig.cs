using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexaVerify.Application;
using NexaVerify.Application.Abstractions;
using NexaVerify.Domain.Tenancy;
using NexaVerify.Domain.Verification;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.IntegrationTests.Verification;

/// <summary>The real persistence stack (DI, interceptors, RLS, seeds) on its own freshly migrated SQL Server database.</summary>
internal sealed class M14Rig : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private M14Rig(ServiceProvider provider, string connectionString)
    {
        _provider = provider;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static async Task<M14Rig> CreateAsync(SqlServerFixture fixture)
    {
        var connection = await fixture.CreateDatabaseAsync();
        await DatabaseBootstrap.MigrateAsync(connection);
        return Build(connection);
    }

    public static M14Rig Build(string connection)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(DatabaseBootstrap.BaseSettings(connection)).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddApplication();
        services.AddInfrastructure(configuration);
        return new M14Rig(services.BuildServiceProvider(), connection);
    }

    public async Task<T> TenantAsync<T>(Guid clientId, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _provider.CreateAsyncScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginTenant(clientId);
        return await action(scope.ServiceProvider);
    }

    public Task TenantAsync(Guid clientId, Func<IServiceProvider, Task> action) =>
        TenantAsync(clientId, async sp =>
        {
            await action(sp);
            return 0;
        });

    public async Task<T> PlatformAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _provider.CreateAsyncScope();
        using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("m14 test");
        return await action(scope.ServiceProvider);
    }

    public Task PlatformAsync(Func<IServiceProvider, Task> action) =>
        PlatformAsync(async sp =>
        {
            await action(sp);
            return 0;
        });

    public async Task<Guid> NewClientAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return await PlatformAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var client = Client.Create("T" + suffix, "Test " + suffix, "ops@test.invalid", "UTC", DateTime.UtcNow);
            db.Clients.Add(client);
            await db.SaveChangesAsync();
            return client.Id;
        });
    }

    public static VerificationRequest NewRequest(Guid clientId, DateTime now, VerificationType type = VerificationType.Pan, string? idempotencyKey = null, DateTime? expiresAt = null) =>
        VerificationRequest.Create(
            Guid.Empty, clientId, type, SubmitSource.Api, VerificationActorType.ApiKey, Guid.NewGuid(), "ref-1", "consent-1", idempotencyKey,
            new byte[32], 1, "corr-" + Guid.NewGuid().ToString("N")[..8], "10.0.0.1", now, expiresAt);

    /// <summary>Runs a SQL statement with the admin connection (no session context, i.e. what a stray connection would see).</summary>
    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
