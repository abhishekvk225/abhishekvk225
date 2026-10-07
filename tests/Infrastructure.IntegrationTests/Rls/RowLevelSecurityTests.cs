using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.IntegrationTests.Rls;

/// <summary>
/// Proves the database itself enforces tenant isolation: every test bypasses the EF query filter
/// (IgnoreQueryFilters / raw SQL / a connection with no tenant) and must still be contained.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class RowLevelSecurityTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private TestDb _db = null!;
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    public RowLevelSecurityTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync(_fixture);
        foreach (var (client, name) in new[] { (_a, "a1"), (_a, "a2"), (_b, "b1") })
        {
            using var scope = _db.Tenant.BeginTenant(client);
            await using var context = _db.NewContext();
            context.Widgets.Add(new Widget { Name = name });
            context.SecretWidgets.Add(new SecretWidget { Name = "s-" + name });
            await context.SaveChangesAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Installer_is_idempotent_and_keeps_isolation_intact()
    {
        await using (var context = _db.NewContext())
        {
            await new NexaVerify.Infrastructure.Persistence.Rls.RowLevelSecurityInstaller(_db.Tenant).InstallAsync(context);
        }

        using var scope = _db.Tenant.BeginTenant(_a);
        await using var tenantContext = _db.NewContext();
        (await tenantContext.Widgets.IgnoreQueryFilters().CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Ignoring_the_ef_filter_still_cannot_read_other_tenants()
    {
        using var scope = _db.Tenant.BeginTenant(_a);
        await using var context = _db.NewContext();

        var rows = await context.Widgets.IgnoreQueryFilters().Select(w => w.Name).ToListAsync();

        rows.OrderBy(n => n).ShouldBe(["a1", "a2"]);
    }

    [Fact]
    public async Task Raw_sql_select_is_contained()
    {
        using var scope = _db.Tenant.BeginTenant(_b);
        await using var context = _db.NewContext();

        var count = await context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [test].[Widgets]").SingleAsync();

        count.ShouldBe(1);
    }

    [Fact]
    public async Task Raw_sql_insert_for_another_tenant_is_blocked()
    {
        using var scope = _db.Tenant.BeginTenant(_a);
        await using var context = _db.NewContext();

        var insert = () => context.Database.ExecuteSqlRawAsync(
            "INSERT INTO [test].[Widgets] (Id, ClientId, Name, CreatedAt, IsActive) VALUES (NEWID(), {0}, 'evil', SYSUTCDATETIME(), 1)",
            _b);

        var ex = await Should.ThrowAsync<SqlException>(insert);
        ex.Number.ShouldBe(33504); // RLS block predicate violation
    }

    [Fact]
    public async Task Raw_sql_update_and_delete_affect_only_own_rows()
    {
        using (_db.Tenant.BeginTenant(_a))
        {
            await using var context = _db.NewContext();
            (await context.Database.ExecuteSqlRawAsync("UPDATE [test].[Widgets] SET Name = 'hacked'")).ShouldBe(2);
            (await context.Database.ExecuteSqlRawAsync("DELETE FROM [test].[Widgets]")).ShouldBe(2);
        }

        using (_db.Tenant.BeginTenant(_b))
        {
            await using var context = _db.NewContext();
            (await context.Widgets.Select(w => w.Name).ToListAsync()).ShouldBe(["b1"]);
        }
    }

    [Fact]
    public async Task Moving_a_row_to_another_tenant_with_raw_sql_is_blocked()
    {
        using var scope = _db.Tenant.BeginTenant(_a);
        await using var context = _db.NewContext();

        var move = () => context.Database.ExecuteSqlRawAsync("UPDATE [test].[Widgets] SET ClientId = {0}", _b);

        await Should.ThrowAsync<SqlException>(move);
    }

    [Fact]
    public async Task Connection_without_any_session_context_sees_nothing()
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM [test].[Widgets]) + (SELECT COUNT(*) FROM [test].[SecretWidgets])";

        ((int)(await command.ExecuteScalarAsync())!).ShouldBe(0);
    }

    [Fact]
    public async Task Platform_session_cannot_read_strict_tables_even_without_ef_filter()
    {
        using var scope = _db.Tenant.BeginPlatform("test");
        await using var context = _db.NewContext();

        (await context.SecretWidgets.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        (await context.Widgets.IgnoreQueryFilters().CountAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task Pooled_connections_never_carry_over_the_previous_tenant()
    {
        for (var i = 0; i < 20; i++)
        {
            var client = i % 2 == 0 ? _a : _b;
            var expected = client == _a ? 2 : 1;
            using var scope = _db.Tenant.BeginTenant(client);
            await using var context = _db.NewContext();
            (await context.Widgets.IgnoreQueryFilters().CountAsync()).ShouldBe(expected);
        }

        // and a context with no tenant right after still sees nothing
        await using var anonymous = _db.NewContext();
        (await anonymous.Widgets.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
    }
}
