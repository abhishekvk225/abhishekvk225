using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using NexaVerify.Application.Abstractions;
using NexaVerify.Infrastructure.Persistence.Guards;
using NexaVerify.Infrastructure.Persistence.Seed;
using NexaVerify.Infrastructure.Persistence.Rls;

namespace NexaVerify.Infrastructure.Persistence.Maintenance;

/// <summary>
/// The deployment-time database procedure: drop the (schema-bound) RLS policy → apply migrations → install the model-derived
/// guards (RLS + append-only triggers) → seed identity data. Run by <c>NexaVerify.Migrator</c> under an administrative
/// principal; never by the request-serving application in production.
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly AppDbContext _db;
    private readonly DatabaseGuardsInstaller _guards;
    private readonly IdentitySeeder _seeder;
    private readonly ITenantScope _scope;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(AppDbContext db, DatabaseGuardsInstaller guards, IdentitySeeder seeder, ITenantScope scope, ILogger<DatabaseInitializer> logger)
    {
        _db = db;
        _guards = guards;
        _seeder = seeder;
        _scope = scope;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var platform = _scope.BeginPlatform("database initialization");

        if (await _db.Database.CanConnectAsync(cancellationToken) && (await _db.Database.GetAppliedMigrationsAsync(cancellationToken)).Any())
        {
            await _guards.DropPolicyAsync(_db, cancellationToken);
        }

        _logger.LogInformation("Applying migrations");
        await _db.Database.MigrateAsync(cancellationToken);

        _logger.LogInformation("Installing database guards");
        await _guards.InstallAsync(_db, cancellationToken);

        _logger.LogInformation("Seeding identity data");
        await _seeder.SeedAsync(cancellationToken);
    }

    /// <summary>The idempotent SQL equivalent for DBA-run deployments: migrations script + guards.</summary>
    public string GenerateScript()
    {
        var migrator = _db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        var guards = DatabaseGuardsInstaller.BuildBatches(_db.GetService<IDesignTimeModel>().Model);
        return string.Join(
            Environment.NewLine + "GO" + Environment.NewLine,
            [RowLevelSecurityScriptBuilder.DropPolicy(), migrator.GenerateScript(options: Microsoft.EntityFrameworkCore.Migrations.MigrationsSqlGenerationOptions.Idempotent), .. guards]);
    }

    /// <summary>
    /// Creates (idempotently) the least-privilege principal the application connects as: data read/write and execute only, no DDL,
    /// no ability to alter the security policy, and DENY on UPDATE/DELETE of append-only tables.
    /// </summary>
    public async Task EnsureApplicationPrincipalAsync(string login, string password, CancellationToken cancellationToken)
    {
        using var platform = _scope.BeginPlatform("create application principal");
        var q = RowLevelSecurityScriptBuilder.Quote;

        await _db.Database.ExecuteSqlRawAsync(
            """
            DECLARE @sql nvarchar(max);
            IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = @login)
            BEGIN
                SET @sql = N'CREATE LOGIN ' + QUOTENAME(@login) + N' WITH PASSWORD = N''' + REPLACE(@password, '''', '''''') + N''', CHECK_POLICY = ON, DEFAULT_DATABASE = ' + QUOTENAME(DB_NAME());
                EXEC (@sql);
            END
            IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @login)
            BEGIN
                SET @sql = N'CREATE USER ' + QUOTENAME(@login) + N' FOR LOGIN ' + QUOTENAME(@login);
                EXEC (@sql);
            END
            SET @sql = N'ALTER ROLE db_datareader ADD MEMBER ' + QUOTENAME(@login) + N'; ALTER ROLE db_datawriter ADD MEMBER ' + QUOTENAME(@login)
                     + N'; GRANT EXECUTE TO ' + QUOTENAME(@login) + N'; GRANT VIEW DEFINITION TO ' + QUOTENAME(@login)
                     + N'; DENY ALTER ANY SECURITY POLICY TO ' + QUOTENAME(@login) + N';';
            EXEC (@sql);
            """,
            [new SqlParameter("@login", login), new SqlParameter("@password", password)],
            cancellationToken);

        foreach (var table in AppendOnlyTriggerBuilder.Tables(_db.GetService<IDesignTimeModel>().Model))
        {
            // Identifiers come from the EF model and the configured login, both bracket-quoted with "]" escaping.
#pragma warning disable EF1002
            await _db.Database.ExecuteSqlRawAsync(
                $"DENY UPDATE, DELETE ON {q(table.Schema)}.{q(table.Table)} TO {q(login)};", cancellationToken);
#pragma warning restore EF1002
        }
    }
}
