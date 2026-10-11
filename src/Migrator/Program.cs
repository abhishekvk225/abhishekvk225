using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexaVerify.Application;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Licensing;
using NexaVerify.Infrastructure;
using NexaVerify.Infrastructure.Persistence.Maintenance;
using Serilog;

// NexaVerify.Migrator — the only component that changes the database schema.
//   migrate (default)  drop RLS policy → apply migrations → install guards → seed identity data
//   script             print the idempotent SQL equivalent (for DBA-run deployments)
//   recover-superadmin break-glass: (re)create the Seed:SuperAdminEmail account as an active Super Admin with Seed:SuperAdminPassword
//   verify-ledger      recompute every license ledger (hash chain, balances, signed checkpoints); exit code 3 when something is broken.
//                      Needs the SAME Encryption__MasterKeyBase64 as the API (the checkpoints are keyed). Used by the restore drill.
//   rotate-master-key  move all master-key-protected data onto Encryption:CurrentKeyId (online, resumable): --dry-run, --batch-size=N,
//                      --max-batches=N, --resume, --confirm-backup (required unless --dry-run). Exit 0 complete, 4 incomplete, 3 ledger broken, 1 failed.
//   rotation-status    counts per key id per artifact type, % complete, safe-to-retire per old key (exit 3 when data references an unknown key id)
//   verify-keyring     decrypt-test a sample under every key + ledger verification (--sample=N); exit 0 ok, 3 ledger broken, 1 keyring failure
//   app-principal      create the least-privilege login the API connects as (Migrator:AppLogin / Migrator:AppPassword)
// Connection string: ConnectionStrings__Default (an administrative principal). Seed: Seed__SuperAdminEmail / Seed__SuperAdminPassword.

var builder = Host.CreateApplicationBuilder(args);
// Docker/Kubernetes secrets: files in /run/secrets become settings (file "ConnectionStrings__Default" = key ConnectionStrings:Default).
builder.Configuration.AddKeyPerFile(builder.Configuration["NEXAVERIFY_SECRETS_DIR"] ?? "/run/secrets", optional: true);
builder.Logging.ClearProviders();
builder.Logging.AddSerilog(new LoggerConfiguration().ReadFrom.Configuration(builder.Configuration).WriteTo.Console().CreateLogger());
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Migrator");
var command = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "migrate";

try
{
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    switch (command)
    {
        case "migrate":
            await initializer.InitializeAsync(CancellationToken.None);
            logger.LogInformation("Database is up to date.");
            return 0;

        case "script":
            Console.WriteLine(initializer.GenerateScript());
            return 0;

        case "recover-superadmin":
            var email = builder.Configuration["Seed:SuperAdminEmail"];
            var recoveryPassword = builder.Configuration["Seed:SuperAdminPassword"];
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(recoveryPassword))
            {
                logger.LogError("Seed:SuperAdminEmail and Seed:SuperAdminPassword are required.");
                return 2;
            }

            await initializer.RecoverSuperAdminAsync(email, recoveryPassword, CancellationToken.None);
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

        case "verify-ledger":
            using (scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("ledger verification (operator command)"))
            {
                var verified = await scope.ServiceProvider.GetRequiredService<ILedgerVerificationService>().RunAsync("cli", null, CancellationToken.None);
                if (verified.IsFailure)
                {
                    logger.LogError("Ledger verification could not run: {Message}", verified.Error!.Message);
                    return 1;
                }

                var report = verified.Value;
                Console.WriteLine($"Ledger verification: {report.LicensesChecked} license(s), {report.EntriesChecked} entries, {report.BrokenLicenses} broken.");
                foreach (var broken in report.Breaks)
                {
                    Console.WriteLine($"  BROKEN license {broken.LicenseId}: {broken.Reason}");
                }

                return report.BrokenLicenses == 0 ? 0 : 3;
            }

        case "rotate-master-key":
            using (scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("master key rotation (operator command)"))
            {
                return await NexaVerify.Migrator.KeyRotationCommands.RotateAsync(scope.ServiceProvider, args, logger, CancellationToken.None);
            }

        case "rotation-status":
            return await NexaVerify.Migrator.KeyRotationCommands.StatusAsync(scope.ServiceProvider, CancellationToken.None);

        case "verify-keyring":
            return await NexaVerify.Migrator.KeyRotationCommands.VerifyAsync(scope.ServiceProvider, args, logger, CancellationToken.None);

        default:
            logger.LogError("Unknown command '{Command}'. Use: migrate | script | app-principal | recover-superadmin | verify-ledger | rotate-master-key | rotation-status | verify-keyring", command);
            return 2;
    }
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Migration failed");
    return 1;
}
