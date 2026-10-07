using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Domain.Auditing;
using NexaVerify.Domain.Identity;
using NexaVerify.Infrastructure.Persistence.Guards;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.IntegrationTests.Persistence;

[Collection(SqlServerCollection.Name)]
public class AppendOnlyTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private TestDb _db = null!;
    private readonly Guid _client = Guid.NewGuid();

    public AppendOnlyTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync(_fixture);
        await _db.EnsureClientAsync(_client);
        using var scope = _db.Tenant.BeginTenant(_client);
        await using var context = _db.NewContext();
        context.AuditLogs.Add(new AuditLog { Action = "test.event", EntityType = "Thing", EntityId = "1", OccurredAt = DateTime.UtcNow, ActorType = AuditActorType.System });
        context.LoginHistory.Add(new LoginHistory { EmailAttempted = "a@b.c", Outcome = LoginOutcome.Success, OccurredAt = DateTime.UtcNow });
        await context.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("UPDATE [audit].[AuditLogs] SET Action = 'tampered'")]
    [InlineData("DELETE FROM [audit].[AuditLogs]")]
    [InlineData("UPDATE [iam].[LoginHistory] SET Outcome = 'Success'")]
    [InlineData("DELETE FROM [iam].[LoginHistory]")]
    public async Task History_tables_reject_update_and_delete_even_for_their_own_tenant(string sql)
    {
        using var scope = _db.Tenant.BeginTenant(_client);
        await using var context = _db.NewContext();

        var ex = await Should.ThrowAsync<SqlException>(() => context.Database.ExecuteSqlRawAsync(sql));

        ex.Number.ShouldBe(51000);
        ex.Message.ShouldContain("append-only");
    }

    [Fact]
    public async Task Inserting_history_still_works_and_rows_are_unchanged()
    {
        using var scope = _db.Tenant.BeginTenant(_client);
        await using var context = _db.NewContext();
        context.AuditLogs.Add(new AuditLog { Action = "second", EntityType = "Thing", EntityId = "2", OccurredAt = DateTime.UtcNow, ActorType = AuditActorType.System });
        await context.SaveChangesAsync();

        (await context.AuditLogs.Select(a => a.Action).OrderBy(a => a).ToListAsync()).ShouldBe(["second", "test.event"]);
    }

    [Fact]
    public async Task Readiness_check_detects_missing_guards()
    {
        using (_db.Tenant.BeginPlatform("test"))
        {
            await using var admin = _db.NewContext();
            await admin.Database.ExecuteSqlRawAsync("DROP SECURITY POLICY [security].[TenantPolicy]");
            var check = new TenantProtectionHealthCheck(admin, Microsoft.Extensions.Logging.Abstractions.NullLogger<TenantProtectionHealthCheck>.Instance, Microsoft.Extensions.Options.Options.Create(new NexaVerify.Infrastructure.Persistence.DatabaseOptions()));

            var result = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());

            result.Status.ShouldBe(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy);
        }
    }

    [Fact]
    public async Task Readiness_check_passes_when_all_guards_are_installed()
    {
        using var scope = _db.Tenant.BeginPlatform("test");
        await using var admin = _db.NewContext();
        var check = new TenantProtectionHealthCheck(admin, Microsoft.Extensions.Logging.Abstractions.NullLogger<TenantProtectionHealthCheck>.Instance, Microsoft.Extensions.Options.Options.Create(new NexaVerify.Infrastructure.Persistence.DatabaseOptions()));

        (await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext())).Status
            .ShouldBe(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy);
    }
}
