using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Common;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.IntegrationTests.Tenancy;

[Collection(SqlServerCollection.Name)]
public class TenantIsolationTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private TestDb _db = null!;
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    public TenantIsolationTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync(_fixture);
        await SeedAsync(_a, "a1", "a2");
        await SeedAsync(_b, "b1");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedAsync(Guid clientId, params string[] names)
    {
        using var scope = _db.Tenant.BeginTenant(clientId);
        await using var context = _db.NewContext();
        foreach (var name in names)
        {
            context.Widgets.Add(new Widget { Name = name });
            context.SecretWidgets.Add(new SecretWidget { Name = "secret-" + name });
        }

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Tenant_sees_only_its_own_rows()
    {
        using var scope = _db.Tenant.BeginTenant(_a);
        await using var context = _db.NewContext();

        var names = await context.Widgets.Select(w => w.Name).ToListAsync();

        names.OrderBy(n => n).ShouldBe(["a1", "a2"]);
    }

    [Fact]
    public async Task No_tenant_in_scope_sees_nothing_fail_closed()
    {
        await using var context = _db.NewContext();

        (await context.Widgets.CountAsync()).ShouldBe(0);
        (await context.SecretWidgets.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Platform_scope_sees_all_tenant_owned_rows()
    {
        using var scope = _db.Tenant.BeginPlatform("test");
        await using var context = _db.NewContext();

        (await context.Widgets.CountAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task Platform_scope_cannot_read_strict_tenant_rows()
    {
        using var scope = _db.Tenant.BeginPlatform("test");
        await using var context = _db.NewContext();

        (await context.SecretWidgets.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Owner_can_read_its_strict_rows()
    {
        using var scope = _db.Tenant.BeginTenant(_b);
        await using var context = _db.NewContext();

        (await context.SecretWidgets.Select(w => w.Name).ToListAsync()).ShouldBe(["secret-b1"]);
    }

    [Fact]
    public async Task Non_tenant_entities_are_unaffected()
    {
        using (_db.Tenant.BeginPlatform("test"))
        {
            await using var seed = _db.NewContext();
            seed.GlobalThings.Add(new GlobalThing { Name = "g" });
            await seed.SaveChangesAsync();
        }

        using var scope = _db.Tenant.BeginTenant(_a);
        await using var context = _db.NewContext();
        (await context.GlobalThings.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Adding_a_row_stamps_the_current_tenant()
    {
        using var scope = _db.Tenant.BeginTenant(_a);
        await using var context = _db.NewContext();
        var widget = new Widget { Name = "new" };
        context.Widgets.Add(widget);

        await context.SaveChangesAsync();

        widget.ClientId.ShouldBe(_a);
    }

    [Fact]
    public async Task Adding_a_row_for_another_tenant_is_rejected()
    {
        using var scope = _db.Tenant.BeginTenant(_a);
        await using var context = _db.NewContext();
        context.Widgets.Add(new Widget { Name = "evil", ClientId = _b });

        await Should.ThrowAsync<TenantViolationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Adding_without_any_tenant_is_rejected()
    {
        await using var context = _db.NewContext();
        context.Widgets.Add(new Widget { Name = "orphan" });

        await Should.ThrowAsync<TenantViolationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Platform_must_set_client_id_explicitly_when_adding()
    {
        using var scope = _db.Tenant.BeginPlatform("test");
        await using var context = _db.NewContext();
        context.Widgets.Add(new Widget { Name = "no-owner" });

        await Should.ThrowAsync<TenantViolationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Platform_can_add_for_an_explicit_tenant()
    {
        using var scope = _db.Tenant.BeginPlatform("test");
        await using var context = _db.NewContext();
        context.Widgets.Add(new Widget { Name = "by-platform", ClientId = _b });

        await context.SaveChangesAsync();

        (await context.Widgets.CountAsync(w => w.ClientId == _b)).ShouldBe(2);
    }

    [Fact]
    public async Task Reassigning_a_row_to_another_tenant_is_rejected_even_for_platform()
    {
        using var scope = _db.Tenant.BeginPlatform("test");
        await using var context = _db.NewContext();
        var widget = await context.Widgets.FirstAsync(w => w.ClientId == _a);
        widget.ClientId = _b;

        await Should.ThrowAsync<TenantViolationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Modifying_or_deleting_another_tenants_tracked_row_is_rejected()
    {
        await using var context = _db.NewContext();
        Widget victim;
        using (_db.Tenant.BeginPlatform("test"))
        {
            victim = await context.Widgets.FirstAsync(w => w.ClientId == _b);
        }

        using var attacker = _db.Tenant.BeginTenant(_a);
        victim.Name = "hacked";
        await Should.ThrowAsync<TenantViolationException>(() => context.SaveChangesAsync());

        context.ChangeTracker.Clear();
        context.Widgets.Remove(victim);
        await Should.ThrowAsync<TenantViolationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Audit_columns_are_stamped_on_add_and_update()
    {
        var actor = Guid.NewGuid();
        _db.User.IsAuthenticated = true;
        _db.User.ActorId = actor;
        var created = _db.Time.GetUtcNow().UtcDateTime;

        Guid id;
        using (_db.Tenant.BeginTenant(_a))
        {
            await using var context = _db.NewContext();
            var widget = new Widget { Name = "audited" };
            context.Widgets.Add(widget);
            await context.SaveChangesAsync();
            id = widget.Id;
            widget.CreatedAt.ShouldBe(created);
            widget.CreatedBy.ShouldBe(actor);
            widget.UpdatedAt.ShouldBeNull();
        }

        _db.Time.Advance(TimeSpan.FromHours(2));
        var editor = Guid.NewGuid();
        _db.User.ActorId = editor;

        using (_db.Tenant.BeginTenant(_a))
        {
            await using var context = _db.NewContext();
            var widget = await context.Widgets.SingleAsync(w => w.Id == id);
            widget.Name = "edited";
            await context.SaveChangesAsync();

            widget.CreatedAt.ShouldBe(created);
            widget.CreatedBy.ShouldBe(actor);
            widget.UpdatedAt.ShouldBe(created.AddHours(2));
            widget.UpdatedBy.ShouldBe(editor);
        }
    }

    [Fact]
    public async Task Created_audit_columns_cannot_be_tampered_with_on_update()
    {
        using var scope = _db.Tenant.BeginTenant(_a);
        await using var context = _db.NewContext();
        var widget = await context.Widgets.FirstAsync();
        var original = widget.CreatedAt;
        widget.CreatedAt = DateTime.UtcNow.AddYears(-10);
        widget.Name = "x";

        await context.SaveChangesAsync();

        await using var verify = _db.NewContext();
        (await verify.Widgets.AsNoTracking().SingleAsync(w => w.Id == widget.Id)).CreatedAt.ShouldBe(original);
    }
}
