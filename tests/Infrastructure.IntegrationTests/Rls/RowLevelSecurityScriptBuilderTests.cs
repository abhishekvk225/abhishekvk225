using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using NexaVerify.Application.Abstractions;
using NexaVerify.Infrastructure.Persistence.Rls;
using NexaVerify.Infrastructure.Tenancy;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.IntegrationTests.Rls;

public class RowLevelSecurityScriptBuilderTests
{
    private static IReadOnlyList<string> BuildBatches()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=none") // never connected: only the model is read
            .Options;
        using var context = new TestDbContext(options, new TenantContext(new StubCurrentUser()));
        return RowLevelSecurityScriptBuilder.Build(context.GetService<IDesignTimeModel>().Model);
    }

    [Fact]
    public void Every_tenant_owned_table_gets_filter_and_block_predicates()
    {
        var policy = BuildBatches().Single(b => b.StartsWith("CREATE SECURITY POLICY", StringComparison.Ordinal));

        policy.ShouldContain("ADD FILTER PREDICATE [security].[fn_TenantFilter]([ClientId]) ON [test].[Widgets]");
        policy.ShouldContain("ADD BLOCK PREDICATE [security].[fn_TenantFilter]([ClientId]) ON [test].[Widgets] AFTER INSERT");
        policy.ShouldContain("ADD BLOCK PREDICATE [security].[fn_TenantFilter]([ClientId]) ON [test].[Widgets] BEFORE DELETE");
    }

    [Fact]
    public void Strict_tables_use_the_strict_function()
    {
        var policy = BuildBatches().Single(b => b.StartsWith("CREATE SECURITY POLICY", StringComparison.Ordinal));

        policy.ShouldContain("[security].[fn_StrictTenantFilter]([ClientId]) ON [test].[SecretWidgets]");
        policy.ShouldNotContain("fn_TenantFilter]([ClientId]) ON [test].[SecretWidgets]");
    }

    [Fact]
    public void Non_tenant_tables_are_not_in_the_policy()
    {
        BuildBatches().Single(b => b.StartsWith("CREATE SECURITY POLICY", StringComparison.Ordinal))
            .ShouldNotContain("GlobalThings");
    }

    [Fact]
    public void Strict_function_has_no_platform_bypass_and_filter_function_does()
    {
        var batches = BuildBatches();
        var strict = batches.Single(b => b.Contains("CREATE OR ALTER FUNCTION [security].[fn_StrictTenantFilter]"));
        var normal = batches.Single(b => b.Contains("CREATE OR ALTER FUNCTION [security].[fn_TenantFilter]"));

        strict.ShouldNotContain("IsPlatform");
        normal.ShouldContain("IsPlatform");
    }

    [Fact]
    public void Policy_is_dropped_before_functions_are_replaced_so_the_script_is_idempotent()
    {
        var batches = BuildBatches().ToList();
        var drop = batches.FindIndex(b => b.Contains("DROP SECURITY POLICY"));
        var function = batches.FindIndex(b => b.Contains("CREATE OR ALTER FUNCTION"));
        var create = batches.FindIndex(b => b.StartsWith("CREATE SECURITY POLICY", StringComparison.Ordinal));

        drop.ShouldBeLessThan(function);
        function.ShouldBeLessThan(create);
    }
}
