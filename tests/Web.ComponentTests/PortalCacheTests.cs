using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NexaVerify.Web.Security;

namespace NexaVerify.Web.ComponentTests;

/// <summary>The portal's shared-store option: configuration rules, the cache chosen per provider, and state that is visible to every node.</summary>
public class PortalCacheTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static PortalCacheOptions Parse(Dictionary<string, string?> values) =>
        Config(values).GetSection(PortalCacheOptions.Section).Get<PortalCacheOptions>() ?? new PortalCacheOptions();

    [Fact]
    public void Memory_is_the_default_and_is_not_shared()
    {
        var options = Parse([]);

        options.Provider.ShouldBe(PortalCacheProvider.Memory);
        options.IsShared.ShouldBeFalse();
        Should.NotThrow(() => PortalCacheRegistration.Validate(options));
    }

    [Fact]
    public void A_shared_provider_needs_its_connection_settings()
    {
        Should.Throw<InvalidOperationException>(() => PortalCacheRegistration.Validate(Parse(new() { ["PortalCache:Provider"] = "SqlServer" })))
            .Message.ShouldContain("PortalCache:SqlServer:ConnectionString");
        Should.Throw<InvalidOperationException>(() => PortalCacheRegistration.Validate(Parse(new() { ["PortalCache:Provider"] = "Redis" })))
            .Message.ShouldContain("PortalCache:Redis:Configuration");
        Should.NotThrow(() => PortalCacheRegistration.Validate(Parse(new()
        {
            ["PortalCache:Provider"] = "SqlServer",
            ["PortalCache:SqlServer:ConnectionString"] = "Server=db;Database=cache;Integrated Security=true",
        })));
        Should.NotThrow(() => PortalCacheRegistration.Validate(Parse(new() { ["PortalCache:Provider"] = "Redis", ["PortalCache:Redis:Configuration"] = "redis:6379" })));
    }

    [Theory]
    [InlineData("dbo]; DROP TABLE x;--", "PortalCache")]
    [InlineData("dbo", "Cache Table")]
    [InlineData("1abc", "PortalCache")]
    public void Table_and_schema_names_must_be_plain_identifiers_because_they_are_used_in_ddl(string schema, string table)
    {
        var options = Parse(new()
        {
            ["PortalCache:Provider"] = "SqlServer",
            ["PortalCache:SqlServer:ConnectionString"] = "Server=db",
            ["PortalCache:SqlServer:SchemaName"] = schema,
            ["PortalCache:SqlServer:TableName"] = table,
        });

        Should.Throw<InvalidOperationException>(() => PortalCacheRegistration.Validate(options)).Message.ShouldContain("identifiers");
    }

    [Fact]
    public void An_unknown_provider_is_refused()
    {
        Should.Throw<InvalidOperationException>(() => PortalCacheRegistration.Validate(new PortalCacheOptions { Provider = (PortalCacheProvider)99 }));
    }

    [Fact]
    public void The_generated_ddl_matches_the_shipped_script_shape()
    {
        var sql = PortalCacheRegistration.CreateTableSql("dbo", "PortalCache");

        sql.ShouldContain("CREATE TABLE [dbo].[PortalCache]");
        sql.ShouldContain("ExpiresAtTime datetimeoffset NOT NULL");
        sql.ShouldContain("COLLATE SQL_Latin1_General_CP1_CS_AS");
        File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "sql", "portal-cache.sql")).ShouldContain("CREATE TABLE [dbo].[PortalCache]");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NexaVerify.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void Each_provider_registers_its_own_distributed_cache()
    {
        IDistributedCache Resolve(Dictionary<string, string?> values)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddPortalCache(Config(values));
            return services.BuildServiceProvider().GetRequiredService<IDistributedCache>();
        }

        Resolve([]).GetType().Name.ShouldBe("MemoryDistributedCache");
        Resolve(new() { ["PortalCache:Provider"] = "Redis", ["PortalCache:Redis:Configuration"] = "localhost:6379" }).ShouldBeAssignableTo<RedisCache>();
        Resolve(new()
        {
            ["PortalCache:Provider"] = "SqlServer",
            ["PortalCache:SqlServer:ConnectionString"] = "Server=127.0.0.1,1;Database=x;User Id=x;Password=x;Encrypt=False",
        }).GetType().Name.ShouldBe("SqlServerCache");
    }

    [Fact]
    public async Task Sessions_and_pending_sign_ins_are_visible_to_a_second_node_sharing_cache_and_key_ring()
    {
        var shared = new SessionFixtures.DictionaryCache();
        var keys = new EphemeralDataProtectionProvider(); // stands in for the shared DataProtection:KeyPath
        var clock = new FakeTimeProvider(SessionFixtures.Start);
        var options = Microsoft.Extensions.Options.Options.Create(SessionFixtures.Options);
        var nodeA = new DistributedSessionStore(shared, keys, clock, options);
        var nodeB = new DistributedSessionStore(shared, keys, clock, options);
        await nodeA.SaveAsync(SessionFixtures.NewSession(clock));

        (await nodeB.GetAsync("sid-1")).ShouldNotBeNull().Email.ShouldBe("admin@nexaverify.test");

        await nodeB.RemoveAsync("sid-1"); // sign-out on the other node ends it everywhere
        (await nodeA.GetAsync("sid-1")).ShouldBeNull();

        var pendingA = new DistributedMfaPendingStore(shared, keys, clock);
        var pendingB = new DistributedMfaPendingStore(shared, keys, clock);
        var id = await pendingA.CreateAsync(new MfaPending("challenge-token", "ada@nexaverify.test", "203.0.113.5", "/dashboard", clock.GetUtcNow().AddMinutes(5)));

        (await pendingB.GetAsync(id)).ShouldNotBeNull().Email.ShouldBe("ada@nexaverify.test");
        await pendingB.RemoveAsync(id);
        (await pendingA.GetAsync(id)).ShouldBeNull("a challenge consumed on one node cannot be replayed on the other");
    }

    [Fact]
    public void The_export_throttle_counts_across_nodes_when_a_shared_cache_is_used()
    {
        var shared = new SessionFixtures.DictionaryCache();
        var clock = new FakeTimeProvider(SessionFixtures.Start);
        var nodeA = new DownloadThrottle(clock, shared);
        var nodeB = new DownloadThrottle(clock, shared);

        for (var i = 0; i < DownloadThrottle.MaxPerWindow; i++)
        {
            (i % 2 == 0 ? nodeA : nodeB).TryAcquire("session-1", out _).ShouldBeTrue();
        }

        nodeA.TryAcquire("session-1", out var wait).ShouldBeFalse();
        nodeB.TryAcquire("session-1", out _).ShouldBeFalse("the other node sees the same window");
        wait.ShouldBeGreaterThan(TimeSpan.Zero);
        nodeB.TryAcquire("session-2", out _).ShouldBeTrue();

        clock.Advance(DownloadThrottle.Window + TimeSpan.FromSeconds(1));
        nodeB.TryAcquire("session-1", out _).ShouldBeTrue("the window reopened");
    }

    [Fact]
    public void The_in_memory_throttle_stays_per_process()
    {
        var clock = new FakeTimeProvider(SessionFixtures.Start);
        var nodeA = new DownloadThrottle(clock);
        var nodeB = new DownloadThrottle(clock);
        for (var i = 0; i < DownloadThrottle.MaxPerWindow; i++)
        {
            nodeA.TryAcquire("s", out _).ShouldBeTrue();
        }

        nodeA.TryAcquire("s", out _).ShouldBeFalse();
        nodeB.TryAcquire("s", out _).ShouldBeTrue("without a shared cache every node counts alone (documented)");
    }
}
