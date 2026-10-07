using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexaVerify.Application;
using NexaVerify.Infrastructure;
using NexaVerify.Infrastructure.Persistence.Maintenance;
using Serilog;

// NexaVerify.Migrator — the only component that changes the database schema.
//   migrate (default)  drop RLS policy → apply migrations → install guards → seed identity data
//   script             print the idempotent SQL equivalent (for DBA-run deployments)
//   app-principal      create the least-privilege login the API connects as (Migrator:AppLogin / Migrator:AppPassword)
// Connection string: ConnectionStrings__Default (an administrative principal). Seed: Seed__SuperAdminEmail / Seed__SuperAdminPassword.

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSerilog(new LoggerConfiguration().ReadFrom.Configuration(builder.Configuration).WriteTo.Console().CreateLogger());
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Migrator");
var command = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "migrate";

try
{
    switch (command)
    {
        case "migrate":
            await initializer.InitializeAsync(CancellationToken.None);
            logger.LogInformation("Database is up to date.");
            return 0;

        case "script":
            Console.WriteLine(initializer.GenerateScript());
            return 0;

        case "app-principal":
            var login = builder.Configuration["Migrator:AppLogin"];
            var password = builder.Configuration["Migrator:AppPassword"];
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
            {
                logger.LogError("Migrator:AppLogin and Migrator:AppPassword are required.");
                return 2;
            }

            await initializer.EnsureApplicationPrincipalAsync(login, password, CancellationToken.None);
            logger.LogInformation("Application principal '{Login}' is ready.", login);
            return 0;

        default:
            logger.LogError("Unknown command '{Command}'. Use: migrate | script | app-principal", command);
            return 2;
    }
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Migration failed");
    return 1;
}
