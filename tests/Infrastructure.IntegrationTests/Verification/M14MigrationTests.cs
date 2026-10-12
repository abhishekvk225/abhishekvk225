using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Application.Abstractions;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Tenancy;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.Infrastructure.Persistence.Maintenance;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.IntegrationTests.Verification;

[Collection(SqlServerCollection.Name)]
public class M14MigrationTests
{
    private const string Previous = "20261011115727_AddEmailOutbox";
    private readonly SqlServerFixture _fixture;

    public M14MigrationTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task M14_applies_on_top_of_an_existing_populated_database_without_losing_data()
    {
        var connection = await _fixture.CreateDatabaseAsync();
        await using (var old = M14Rig.Build(connection))
        {
            Guid clientId = Guid.Empty;
            await old.PlatformAsync(async sp =>
            {
                var db = sp.GetRequiredService<AppDbContext>();
                await db.GetService<IMigrator>().MigrateAsync(Previous);
                (await db.Database.GetAppliedMigrationsAsync()).ShouldNotContain(m => m.EndsWith("M14Verification", StringComparison.Ordinal));
                var client = Client.Create("OLD1", "Old client", "o@test.invalid", "UTC", DateTime.UtcNow);
                db.Clients.Add(client);
                db.CostRules.Add(CostRule.Create(null, MeteredOperation.Verify, 7, ChargePolicy.OnCompleted, DateTime.UtcNow));
                await db.SaveChangesAsync();
                clientId = client.Id;
            });
            (await old.ScalarAsync<int>("SELECT COUNT(*) FROM sys.schemas WHERE name = 'verification'")).ShouldBe(0);
        }

        // the deployment procedure: migrate -> guards -> seed (idempotent, so a second run must be a no-op)
        await DatabaseBootstrap.MigrateAsync(connection);
        await DatabaseBootstrap.MigrateAsync(connection);

        await using var rig = M14Rig.Build(connection);
        (await rig.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'verification'")).ShouldBe(8);
        (await rig.PlatformAsync(sp => sp.GetRequiredService<AppDbContext>().Clients.CountAsync(c => c.Code == "OLD1"))).ShouldBe(1);
        (await rig.ScalarAsync<int>("SELECT Credits FROM licensing.CostRules WHERE PlanId IS NULL AND Operation = 'Verify'")).ShouldBe(7); // an existing operator price is untouched
        (await rig.ScalarAsync<int>("SELECT COUNT(*) FROM licensing.CostRules WHERE PlanId IS NULL AND Operation IN ('BankStatementExtraction','PanExtraction','PanProviderCheck','PassportExtraction','PassportProviderCheck')")).ShouldBe(5);
        (await rig.ScalarAsync<int>("SELECT COUNT(*) FROM verification.ModuleSettings")).ShouldBe(3);
        // RLS covers every verification table that carries a ClientId, and the events trigger exists
        (await rig.ScalarAsync<int>("SELECT COUNT(DISTINCT t.name) FROM sys.security_predicates p JOIN sys.objects t ON t.object_id = p.target_object_id WHERE SCHEMA_NAME(t.schema_id) = 'verification'")).ShouldBe(7);
        (await rig.ScalarAsync<int>("SELECT COUNT(*) FROM sys.triggers WHERE name = 'trg_Events_AppendOnly'")).ShouldBe(1);
        await rig.PlatformAsync(async sp => (await sp.GetRequiredService<AppDbContext>().Database.GetPendingMigrationsAsync()).ShouldBeEmpty());
    }

    [Fact]
    public async Task Idempotent_deployment_script_contains_the_m14_migration_and_all_guards()
    {
        await using var rig = await M14Rig.CreateAsync(_fixture);
        var script = await rig.PlatformAsync(sp => Task.FromResult(sp.GetRequiredService<DatabaseInitializer>().GenerateScript()));
        script.ShouldContain("M14Verification");
        script.ShouldContain("trg_Events_AppendOnly");
        script.ShouldContain("[verification].[Documents]");
    }
}
