extern alias WebApp;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NexaVerify.TestSupport;
using WebApp::NexaVerify.Web.Security;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>Two portal nodes on one SQL Server cache table and one key ring: sessions, pending MFA sign-ins and the export throttle are shared.</summary>
[Collection(SqlServerCollection.Name)]
public class PortalSharedStoreTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "nv-portal-keys-" + Guid.NewGuid().ToString("N"));
    private string _connectionString = string.Empty;
    private readonly List<ServiceProvider> _nodes = [];

    public PortalSharedStoreTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => _connectionString = await _fixture.CreateDatabaseAsync();

    public async Task DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            await node.DisposeAsync();
        }

        if (Directory.Exists(_keyDirectory))
        {
            Directory.Delete(_keyDirectory, recursive: true);
        }
    }

    private async Task<ServiceProvider> NodeAsync(bool ensureTable = true)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PortalCache:Provider"] = "SqlServer",
            ["PortalCache:SqlServer:ConnectionString"] = _connectionString,
            ["PortalCache:SqlServer:SchemaName"] = "portal",
            ["PortalCache:SqlServer:TableName"] = "Cache",
            ["PortalCache:SqlServer:EnsureTable"] = ensureTable ? "true" : "false",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDataProtection().SetApplicationName("NexaVerify.Portal").PersistKeysToFileSystem(new DirectoryInfo(_keyDirectory));
        services.AddPortalCache(configuration);
        services.Configure<SessionOptions>(_ => { });
        services.AddSingleton<ISessionStore, DistributedSessionStore>();
        services.AddSingleton<IMfaPendingStore, DistributedMfaPendingStore>();
        services.AddSingleton(sp => new DownloadThrottle(sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>()));
        var provider = services.BuildServiceProvider();
        _nodes.Add(provider);
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }

        return provider;
    }

    private static PortalSession NewSession(string id) => new()
    {
        Id = id,
        AccessToken = "access-token-value",
        AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        RefreshToken = "refresh-token-value",
        UserId = Guid.NewGuid(),
        Email = "ada@nexaverify.test",
        FullName = "Ada",
        Portal = PortalKinds.Client,
        Roles = ["ClientAdmin"],
        Permissions = ["dashboard.client"],
        CreatedAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow,
        AbsoluteExpiresAt = DateTimeOffset.UtcNow.AddHours(12),
    };

    [Fact]
    public async Task The_table_is_created_on_start_and_starting_a_second_node_is_harmless()
    {
        await NodeAsync();
        await NodeAsync(); // idempotent

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'portal' AND t.name = 'Cache'";
        ((int)(await command.ExecuteScalarAsync())!).ShouldBe(1);
    }

    [Fact]
    public async Task A_session_created_on_one_node_is_valid_on_the_other_and_sign_out_ends_it_everywhere()
    {
        var a = await NodeAsync();
        var b = await NodeAsync();
        var id = DistributedSessionStore.NewId();
        await a.GetRequiredService<ISessionStore>().SaveAsync(NewSession(id));

        var seenByB = await b.GetRequiredService<ISessionStore>().GetAsync(id);

        seenByB.ShouldNotBeNull().Email.ShouldBe("ada@nexaverify.test");
        await b.GetRequiredService<ISessionStore>().UpdateAsync(id, s => s with { Email = "ada.new@nexaverify.test" });
        (await a.GetRequiredService<ISessionStore>().GetAsync(id)).ShouldNotBeNull().Email.ShouldBe("ada.new@nexaverify.test");
        await b.GetRequiredService<ISessionStore>().RemoveAsync(id);
        (await a.GetRequiredService<ISessionStore>().GetAsync(id)).ShouldBeNull();
    }

    [Fact]
    public async Task What_is_stored_in_the_table_is_ciphertext()
    {
        var a = await NodeAsync();
        var id = DistributedSessionStore.NewId();
        await a.GetRequiredService<ISessionStore>().SaveAsync(NewSession(id));

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM portal.Cache WHERE Id = @id";
        command.Parameters.AddWithValue("@id", "nv:session:" + id);
        var raw = (byte[])(await command.ExecuteScalarAsync())!;
        var text = System.Text.Encoding.UTF8.GetString(raw);

        text.ShouldNotContain("access-token-value");
        text.ShouldNotContain("refresh-token-value");
        text.ShouldNotContain("ada@nexaverify.test");
    }

    [Fact]
    public async Task A_pending_mfa_sign_in_started_on_one_node_completes_on_another_exactly_once()
    {
        var a = await NodeAsync();
        var b = await NodeAsync();
        var id = await a.GetRequiredService<IMfaPendingStore>().CreateAsync(
            new MfaPending("api-challenge-token", "ada@nexaverify.test", "203.0.113.9", "/dashboard", DateTimeOffset.UtcNow.AddMinutes(5)));

        var onB = await b.GetRequiredService<IMfaPendingStore>().GetAsync(id);

        onB.ShouldNotBeNull().Email.ShouldBe("ada@nexaverify.test");
        await b.GetRequiredService<IMfaPendingStore>().RemoveAsync(id);
        (await a.GetRequiredService<IMfaPendingStore>().GetAsync(id)).ShouldBeNull();
    }

    [Fact]
    public async Task The_export_throttle_window_is_shared_by_both_nodes()
    {
        var a = await NodeAsync();
        var b = await NodeAsync();
        var key = "session-" + Guid.NewGuid().ToString("N");
        if (DateTime.UtcNow.Second >= 54)
        {
            await Task.Delay(TimeSpan.FromSeconds(8)); // the window is one minute from the first call
        }

        for (var i = 0; i < DownloadThrottle.MaxPerWindow; i++)
        {
            (i % 2 == 0 ? a : b).GetRequiredService<DownloadThrottle>().TryAcquire(key, out _).ShouldBeTrue();
        }

        a.GetRequiredService<DownloadThrottle>().TryAcquire(key, out _).ShouldBeFalse();
        b.GetRequiredService<DownloadThrottle>().TryAcquire(key, out var retry).ShouldBeFalse();
        retry.ShouldBeGreaterThan(TimeSpan.Zero);
    }
}
