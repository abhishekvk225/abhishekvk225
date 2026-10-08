using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Application;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Licensing;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Infrastructure;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>
/// The restore drill as an automated test (docs/runbooks/backup-restore.md, deploy/scripts/backup-restore-drill.sh): back a live
/// database up, restore it under another name, and run the credit-ledger verification against the restored copy with the production
/// master key. A restore that cannot prove its ledger is not a restore.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class BackupRestoreDrillTests : UsageTestBase
{
    private static readonly string MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly SqlServerFixture _fixture;
    private readonly List<string> _restored = [];

    public BackupRestoreDrillTests(SqlServerFixture fixture)
        : base(fixture)
    {
        _fixture = fixture;
    }

    // The same master key the "production" API uses: the ledger checkpoints are keyed with it.
    protected override IReadOnlyDictionary<string, string>? Settings => new Dictionary<string, string> { ["Encryption:MasterKeyBase64"] = MasterKey };

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await using var admin = new SqlConnection(new SqlConnectionStringBuilder(App.ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await admin.OpenAsync();
        foreach (var name in _restored)
        {
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>BACKUP ... WITH COPY_ONLY, CHECKSUM → RESTORE VERIFYONLY → RESTORE under a new name. Returns the restored database's connection string.</summary>
    private async Task<string> BackupAndRestoreAsync()
    {
        var source = new SqlConnectionStringBuilder(App.ConnectionString);
        var restoredName = "nv_drill_" + Guid.NewGuid().ToString("N");
        _restored.Add(restoredName);

        await using var admin = new SqlConnection(new SqlConnectionStringBuilder(App.ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await admin.OpenAsync();
        var backupDirectory = (await ScalarAsync(admin, "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(400))")).TrimEnd('/');
        var dataDirectory = (await ScalarAsync(admin, "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(400))")).TrimEnd('/');
        var file = $"{backupDirectory}/{restoredName}.bak";

        await using (var backup = admin.CreateCommand())
        {
            backup.CommandTimeout = 300;
            backup.CommandText = $"BACKUP DATABASE [{source.InitialCatalog}] TO DISK = N'{file}' WITH COPY_ONLY, CHECKSUM, COMPRESSION, INIT";
            await backup.ExecuteNonQueryAsync();
        }

        await using (var verify = admin.CreateCommand())
        {
            verify.CommandText = $"RESTORE VERIFYONLY FROM DISK = N'{file}' WITH CHECKSUM";
            await verify.ExecuteNonQueryAsync();
        }

        var moves = new List<string>();
        await using (var list = admin.CreateCommand())
        {
            list.CommandText = $"RESTORE FILELISTONLY FROM DISK = N'{file}'";
            await using var reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var logical = reader.GetString(reader.GetOrdinal("LogicalName"));
                var isLog = reader.GetString(reader.GetOrdinal("Type")) == "L";
                moves.Add($"MOVE N'{logical}' TO N'{dataDirectory}/{restoredName}_{logical}.{(isLog ? "ldf" : "mdf")}'");
            }
        }

        await using (var restore = admin.CreateCommand())
        {
            restore.CommandTimeout = 300;
            restore.CommandText = $"RESTORE DATABASE [{restoredName}] FROM DISK = N'{file}' WITH CHECKSUM, RECOVERY, {string.Join(", ", moves)}";
            await restore.ExecuteNonQueryAsync();
        }

        await using (var check = admin.CreateCommand())
        {
            check.CommandTimeout = 300;
            check.CommandText = $"DBCC CHECKDB (N'{restoredName}') WITH NO_INFOMSGS, ALL_ERRORMSGS";
            await check.ExecuteNonQueryAsync();
        }

        return new SqlConnectionStringBuilder(App.ConnectionString) { InitialCatalog = restoredName }.ConnectionString;
    }

    /// <summary>What <c>Migrator verify-ledger</c> does: the same services, pointed at the restored database with the given master key.</summary>
    private static async Task<LedgerVerificationReportDto> VerifyRestoredLedgerAsync(string connectionString, string masterKeyBase64)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connectionString,
            ["Encryption:MasterKeyBase64"] = masterKeyBase64,
            ["Jwt:AllowEphemeralKey"] = "true",
            ["PasswordHashing:IterationCount"] = "10000",
        });
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);
        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("restore drill");
        var result = await scope.ServiceProvider.GetRequiredService<ILedgerVerificationService>().RunAsync("cli", null, default);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.Value;
    }

    private async Task<(Tenant A, Tenant B)> BusyPlatformAsync()
    {
        var a = await NewTenantAsync("RD1", credits: 100);
        var b = await NewTenantAsync("RD2", credits: 100);
        await RunRecognitionsAsync(a, seed: 10);
        await RunRecognitionsAsync(b, seed: 70);
        return (a, b);
    }

    private async Task AnchorTheLedgersAsync()
    {
        var run = await App.PostAsync("/api/v1/admin/licensing/verify-ledger", null, Platform.AccessToken);
        run.IsSuccessStatusCode.ShouldBeTrue();
        for (var i = 0; i < 300; i++)
        {
            await Task.Delay(100);
            if ((await App.WithDbAsync(db => db.LedgerVerificationRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Select(r => r.Status.ToString()).FirstAsync())) != "Running")
            {
                return;
            }
        }

        throw new TimeoutException("The anchoring run did not finish.");
    }

    [Fact]
    public async Task A_restored_backup_passes_the_ledger_verification_with_the_original_master_key()
    {
        var (a, b) = await BusyPlatformAsync();
        await AnchorTheLedgersAsync();
        (await App.WithDbAsync(db => db.LedgerCheckpoints.CountAsync())).ShouldBe(2);

        var restored = await BackupAndRestoreAsync();
        var report = await VerifyRestoredLedgerAsync(restored, MasterKey);

        report.LicensesChecked.ShouldBe(2);
        report.EntriesChecked.ShouldBe(2 * (1 + 5));
        report.BrokenLicenses.ShouldBe(0, string.Join("; ", report.Breaks.Select(x => x.Reason)));
        a.LicenseId.ShouldNotBe(b.LicenseId);
    }

    [Fact]
    public async Task The_restored_copy_holds_the_same_data_and_the_source_is_untouched()
    {
        await BusyPlatformAsync();
        var restored = await BackupAndRestoreAsync();

        async Task<Dictionary<string, long>> CountsAsync(string connectionString)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // sys.partitions is metadata: row-level security does not hide rows from it
            command.CommandText = "SELECT s.name + N'.' + t.name, SUM(p.rows) FROM sys.partitions p JOIN sys.tables t ON t.object_id = p.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE p.index_id IN (0, 1) GROUP BY s.name, t.name";
            await using var reader = await command.ExecuteReaderAsync();
            var counts = new Dictionary<string, long>();
            while (await reader.ReadAsync())
            {
                counts[reader.GetString(0)] = reader.GetInt64(1);
            }

            return counts;
        }

        var source = await CountsAsync(App.ConnectionString);
        var copy = await CountsAsync(restored);

        // The request log is written by a background queue, so a few rows may land after the backup started: it can only be behind.
        const string AsyncTable = "api.ApiRequestLogs";
        copy[AsyncTable].ShouldBeLessThanOrEqualTo(source[AsyncTable]);
        copy.Where(c => c.Key != AsyncTable).OrderBy(c => c.Key).ToList().ShouldBe(source.Where(c => c.Key != AsyncTable).OrderBy(c => c.Key).ToList());
        copy["licensing.LicenseTransactions"].ShouldBeGreaterThan(0);
        copy["face.FaceTemplates"].ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_restore_with_the_wrong_master_key_is_reported_as_a_failed_ledger_check_not_as_success()
    {
        await BusyPlatformAsync();
        await AnchorTheLedgersAsync();
        var restored = await BackupAndRestoreAsync();

        var report = await VerifyRestoredLedgerAsync(restored, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        report.BrokenLicenses.ShouldBe(2);
        report.Breaks.ShouldAllBe(x => x.Reason.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_backup_whose_ledger_was_altered_before_it_was_taken_fails_the_check_on_the_restored_copy()
    {
        var (a, _) = await BusyPlatformAsync();
        await AnchorTheLedgersAsync();
        await App.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE licensing.LicenseTransactions DISABLE TRIGGER trg_LicenseTransactions_AppendOnly");
            try
            {
                var victim = await db.LicenseTransactions.AsNoTracking().Where(t => t.LicenseId == a.LicenseId).OrderBy(t => t.Id).Skip(2).Select(t => t.Id).FirstAsync();
                (await db.Database.ExecuteSqlAsync($"UPDATE licensing.LicenseTransactions SET Reason = N'forged' WHERE Id = {victim}")).ShouldBe(1);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE licensing.LicenseTransactions ENABLE TRIGGER trg_LicenseTransactions_AppendOnly");
            }

            return true;
        });
        var restored = await BackupAndRestoreAsync();

        var report = await VerifyRestoredLedgerAsync(restored, MasterKey);

        report.BrokenLicenses.ShouldBe(1);
        report.Breaks.Single().LicenseId.ShouldBe(a.LicenseId);
    }
}
