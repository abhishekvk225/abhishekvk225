using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Common;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.IntegrationTests.Tenancy;

[Collection(SqlServerCollection.Name)]
public class TenantRootTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private TestDb _db = null!;
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    public TenantRootTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync(_fixture);
        await _db.EnsureClientAsync(_a);
        await _db.EnsureClientAsync(_b);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_tenant_sees_only_its_own_client_record_and_platform_sees_all()
    {
        using (_db.Tenant.BeginTenant(_a))
        {
            await using var context = _db.NewContext();
            (await context.Clients.Select(c => c.Id).ToListAsync()).ShouldBe([_a]);
            (await context.Clients.IgnoreQueryFilters().CountAsync()).ShouldBe(1); // row-level security, not just the EF filter
        }

        using (_db.Tenant.BeginPlatform("test"))
        {
            await using var context = _db.NewContext();
            (await context.Clients.CountAsync()).ShouldBe(2);
        }

        await using var anonymous = _db.NewContext();
        (await anonymous.Clients.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_tenant_can_edit_its_own_record_but_not_anothers_nor_add_or_delete()
    {
        using (_db.Tenant.BeginTenant(_a))
        {
            await using var own = _db.NewContext();
            var mine = await own.Clients.SingleAsync();
            mine.Name = "Renamed by tenant";
            await own.SaveChangesAsync();

            await using var other = _db.NewContext();
            using (_db.Tenant.BeginPlatform("load victim"))
            {
                // load B's row under platform scope, then try to save it as tenant A
            }
        }

        Domain.Tenancy.Client victim;
        using (_db.Tenant.BeginPlatform("test"))
        {
            await using var loader = _db.NewContext();
            victim = await loader.Clients.SingleAsync(c => c.Id == _b);
        }

        using (_db.Tenant.BeginTenant(_a))
        {
            await using var attacker = _db.NewContext();
            attacker.Attach(victim);
            victim.Name = "Hijacked";
            attacker.Entry(victim).Property(c => c.Name).IsModified = true;
            await Should.ThrowAsync<TenantViolationException>(() => attacker.SaveChangesAsync());

            await using var adder = _db.NewContext();
            adder.Clients.Add(Domain.Tenancy.Client.Create("NEWONE", "New", "n@n.test", "UTC", DateTime.UtcNow));
            await Should.ThrowAsync<TenantViolationException>(() => adder.SaveChangesAsync());

            await using var deleter = _db.NewContext();
            var mine = await deleter.Clients.SingleAsync();
            deleter.Clients.Remove(mine);
            await Should.ThrowAsync<TenantViolationException>(() => deleter.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Raw_sql_cannot_touch_other_clients_records()
    {
        using var scope = _db.Tenant.BeginTenant(_a);
        await using var context = _db.NewContext();

        (await context.Database.ExecuteSqlRawAsync("UPDATE tenancy.Clients SET Name = 'x'")).ShouldBe(1);
        (await context.Database.ExecuteSqlRawAsync("DELETE FROM tenancy.Clients WHERE Id <> {0}", _a)).ShouldBe(0);
    }

    [Fact]
    public void The_platform_client_has_the_well_known_id_and_cannot_change_status()
    {
        var system = Domain.Tenancy.Client.CreateSystem(DateTime.UtcNow);

        system.Id.ShouldBe(Domain.Common.PlatformTenant.ClientId);
        system.IsSystem.ShouldBeTrue();
        Should.Throw<Domain.Common.DomainException>(() => system.Suspend("no", null, DateTime.UtcNow)).Code.ShouldBe("CLIENT_SYSTEM_IMMUTABLE");
    }
}
