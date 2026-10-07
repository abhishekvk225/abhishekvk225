using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexaVerify.Application;
using NexaVerify.Infrastructure;
using NexaVerify.Infrastructure.Persistence.Maintenance;

namespace NexaVerify.TestSupport;

/// <summary>Runs the same procedure as <c>NexaVerify.Migrator</c> (migrate → guards → seed) against a fresh test database.</summary>
public static class DatabaseBootstrap
{
    public const string SuperAdminEmail = "root@nexaverify.test";
    public const string SuperAdminPassword = "Initial-Passphrase-42";

    public static IReadOnlyDictionary<string, string?> BaseSettings(string connectionString) => new Dictionary<string, string?>
    {
        ["ConnectionStrings:Default"] = connectionString,
        ["Jwt:AllowEphemeralKey"] = "true",
        ["PasswordHashing:IterationCount"] = "10000",
        ["Seed:SuperAdminEmail"] = SuperAdminEmail,
        ["Seed:SuperAdminPassword"] = SuperAdminPassword,
    };

    public static async Task MigrateAsync(string connectionString)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(BaseSettings(connectionString)).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddApplication();
        services.AddInfrastructure(configuration);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);
    }

    public static async Task CreateApplicationPrincipalAsync(string connectionString, string login, string password)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(BaseSettings(connectionString)).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddApplication();
        services.AddInfrastructure(configuration);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().EnsureApplicationPrincipalAsync(login, password, CancellationToken.None);
    }
}
