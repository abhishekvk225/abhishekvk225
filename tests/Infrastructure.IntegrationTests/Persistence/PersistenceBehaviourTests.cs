using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Common;
using NexaVerify.Infrastructure.Persistence.Rls;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.IntegrationTests.Persistence;

[Collection(SqlServerCollection.Name)]
public class PersistenceBehaviourTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private TestDb _db = null!;
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    public PersistenceBehaviourTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => _db = await TestDb.CreateAsync(_fixture);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Platform_scope_cannot_write_strict_rows_and_gets_a_tenant_violation_not_a_db_error()
    {
        using var scope = _db.Tenant.BeginPlatform("test");
        await using var context = _db.NewContext();
        context.SecretWidgets.Add(new SecretWidget { Name = "x", ClientId = _a });

        await Should.ThrowAsync<TenantViolationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Transaction_commits_all_work_and_rolls_back_on_failure()
    {
        using (_db.Tenant.BeginTenant(_a))
        {
            await using var context = _db.NewContext();
            await context.ExecuteInTransactionAsync(async ct =>
            {
                context.Widgets.Add(new Widget { Name = "kept" });
                await context.SaveChangesAsync(ct);
                return 1;
            });

            await Should.ThrowAsync<InvalidOperationException>(() => context.ExecuteInTransactionAsync<int>(async ct =>
            {
                context.Widgets.Add(new Widget { Name = "rolled-back" });
                await context.SaveChangesAsync(ct);
                throw new InvalidOperationException("boom");
            }));
        }

        using (_db.Tenant.BeginTenant(_a))
        {
            await using var verify = _db.NewContext();
            (await verify.Widgets.Select(w => w.Name).ToListAsync()).ShouldBe(["kept"]);
        }
    }

    [Fact]
    public async Task Nested_transactions_join_the_outer_one()
    {
        using (_db.Tenant.BeginTenant(_a))
        {
            await using var context = _db.NewContext();
            var outer = () => context.ExecuteInTransactionAsync(async ct =>
            {
                await context.ExecuteInTransactionAsync(async inner =>
                {
                    context.Widgets.Add(new Widget { Name = "inner" });
                    await context.SaveChangesAsync(inner);
                    return 0;
                }, ct);
                throw new InvalidOperationException("outer fails after inner succeeded");
                #pragma warning disable CS0162
                return 0;
                #pragma warning restore CS0162
            });

            await Should.ThrowAsync<InvalidOperationException>(outer);
        }

        using (_db.Tenant.BeginTenant(_a))
        {
            await using var verify = _db.NewContext();
            (await verify.Widgets.CountAsync()).ShouldBe(0);
        }
    }

    [Fact]
    public async Task Switching_scope_inside_an_open_transaction_still_applies_the_new_tenant_to_row_level_security()
    {
        foreach (var (client, name) in new[] { (_a, "a1"), (_b, "b1") })
        {
            using var scope = _db.Tenant.BeginTenant(client);
            await using var context = _db.NewContext();
            context.Widgets.Add(new Widget { Name = name });
            await context.SaveChangesAsync();
        }

        await using var shared = _db.NewContext();
        await using var transaction = await shared.Database.BeginTransactionAsync();

        using (_db.Tenant.BeginPlatform("test"))
        {
            (await shared.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [test].[Widgets]").SingleAsync()).ShouldBe(2);
        }

        using (_db.Tenant.BeginTenant(_a))
        {
            // same open connection + transaction: raw SQL must now be contained to tenant A by RLS
            (await shared.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [test].[Widgets]").SingleAsync()).ShouldBe(1);
        }

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Unique_violations_are_translated()
    {
        using var scope = _db.Tenant.BeginTenant(_a);
        await using (var first = _db.NewContext())
        {
            first.Gadgets.Add(new Gadget { Code = "G1" });
            await first.SaveChangesAsync();
        }

        await using var second = _db.NewContext();
        second.Gadgets.Add(new Gadget { Code = "G1" });

        await Should.ThrowAsync<UniqueConstraintViolationException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Concurrent_updates_are_translated()
    {
        using var scope = _db.Tenant.BeginTenant(_a);
        await using (var seed = _db.NewContext())
        {
            seed.Gadgets.Add(new Gadget { Code = "G2" });
            await seed.SaveChangesAsync();
        }

        await using var one = _db.NewContext();
        await using var two = _db.NewContext();
        var g1 = await one.Gadgets.SingleAsync(g => g.Code == "G2");
        var g2 = await two.Gadgets.SingleAsync(g => g.Code == "G2");
        g1.State = GadgetState.Ready;
        await one.SaveChangesAsync();
        g2.State = GadgetState.Ready;

        await Should.ThrowAsync<ConcurrencyConflictException>(() => two.SaveChangesAsync());
    }

    [Fact]
    public async Task Enum_columns_are_stored_as_strings_with_a_check_constraint()
    {
        using var scope = _db.Tenant.BeginTenant(_a);
        await using var context = _db.NewContext();
        context.Gadgets.Add(new Gadget { Code = "G3", State = GadgetState.Ready });
        await context.SaveChangesAsync();

        (await context.Database.SqlQueryRaw<string>("SELECT [State] AS [Value] FROM [test].[Gadgets]").SingleAsync()).ShouldBe("Ready");

        var bad = () => context.Database.ExecuteSqlRawAsync(
            "INSERT INTO [test].[Gadgets] (Id, ClientId, Code, State, CreatedAt, IsActive) VALUES (NEWID(), {0}, 'BAD', 'Nonsense', SYSUTCDATETIME(), 1)", _a);
        (await Should.ThrowAsync<SqlException>(bad)).Number.ShouldBe(547);
    }

    [Fact]
    public async Task Timestamps_are_datetime2_3()
    {
        await using var context = _db.NewContext();
        using var scope = _db.Tenant.BeginPlatform("test");

        var type = await context.Database
            .SqlQueryRaw<string>("SELECT DATA_TYPE + '(' + CAST(DATETIME_PRECISION AS varchar(5)) + ')' AS [Value] FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='test' AND TABLE_NAME='Widgets' AND COLUMN_NAME='CreatedAt'")
            .SingleAsync();

        type.ShouldBe("datetime2(3)");
    }

    [Fact]
    public async Task A_failed_rls_reinstall_rolls_back_and_keeps_isolation()
    {
        foreach (var client in new[] { _a, _b })
        {
            using var scope = _db.Tenant.BeginTenant(client);
            await using var context = _db.NewContext();
            context.Widgets.Add(new Widget { Name = client.ToString() });
            await context.SaveChangesAsync();
        }

        await using (var admin = _db.NewContext())
        {
            var installer = new RowLevelSecurityInstaller(_db.Tenant);
            var failing = new[] { RowLevelSecurityScriptBuilder.DropPolicy(), "SELECT 1/0;" };
            await Should.ThrowAsync<SqlException>(() => installer.ApplyAsync(admin, failing));
        }

        using var scopeA = _db.Tenant.BeginTenant(_a);
        await using var verify = _db.NewContext();
        (await verify.Widgets.IgnoreQueryFilters().CountAsync()).ShouldBe(1); // RLS still contains tenant A
    }
}
