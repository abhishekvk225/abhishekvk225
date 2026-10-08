using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Domain.Api;
using NexaVerify.Infrastructure.Background;
using NexaVerify.Infrastructure.Platform;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>
/// Rate-limit and daily-quota counters shared between API nodes: several limiter instances (and two whole API hosts) on ONE database
/// must enforce the limit together, never beyond it.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class SharedCountersTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Noon = new(2030, 6, 15, 12, 0, 20, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;
    private AuthApp _app = null!;
    private LoginResponse _platform = null!;

    public SharedCountersTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _app = await AuthApp.CreateAsync(_fixture, new Dictionary<string, string> { ["Counters:Shared"] = "true" });
        _platform = await _app.SuperAdminAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private SqlCounterBackend Backend() => new(new SharedCounterConnection(_app.ConnectionString));

    private SharedWindowCounters Node(TimeProvider time) =>
        new(Backend(), Options.Create(new SharedCounterOptions()), time, NullLogger<SharedWindowCounters>.Instance);

    private ApiUsageLimiter Limiter(TimeProvider time) => new(_app.Factory.Services.GetRequiredService<IServiceScopeFactory>(), time, Node(time));

    private async Task<Guid> ClientWithSettingsAsync(string code, int perMinute, long perDay)
    {
        var (client, _) = await _app.OnboardClientAsync(_platform.AccessToken, code, $"a@{code.ToLowerInvariant()}.test");
        var set = await _app.PutAsync($"/api/v1/admin/clients/{client.Id}/settings",
            new UpdateSettingsRequest(new Dictionary<string, JsonElement>
            {
                [SettingKeys.Api.RateLimitPerMinute] = JsonSerializer.SerializeToElement(perMinute),
                [SettingKeys.Api.DailyQuota] = JsonSerializer.SerializeToElement(perDay),
            }), _platform.AccessToken);
        set.StatusCode.ShouldBe(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());
        return client.Id;
    }

    [Fact]
    public async Task Two_limiters_on_one_database_share_the_per_minute_budget_of_a_credential()
    {
        var clientId = await ClientWithSettingsAsync("SC1", perMinute: 6, perDay: 100_000);
        var time = new FakeTimeProvider(Noon);
        var nodeA = Limiter(time);
        var nodeB = Limiter(time);
        var credential = Guid.NewGuid();

        var allowed = 0;
        (NexaVerify.Application.Common.Error? Error, TimeSpan RetryAfter) last = default;
        for (var i = 0; i < 20; i++)
        {
            last = await (i % 2 == 0 ? nodeA : nodeB).TryAcquireAsync(clientId, credential, null, default);
            if (last.Error is null)
            {
                allowed++;
            }
        }

        allowed.ShouldBe(6, "alternating across two nodes must still allow exactly the limit");
        last.Error.ShouldNotBeNull().Code.ShouldBe(ErrorCodes.RateLimited);
        last.RetryAfter.ShouldBeInRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));

        time.Advance(TimeSpan.FromSeconds(45)); // next minute
        (await nodeB.TryAcquireAsync(clientId, credential, null, default)).Error.ShouldBeNull();
    }

    [Fact]
    public async Task Two_limiters_share_the_daily_quota_of_a_client_whatever_credential_calls()
    {
        var clientId = await ClientWithSettingsAsync("SC2", perMinute: 1000, perDay: 9);
        var time = new FakeTimeProvider(Noon);
        var nodeA = Limiter(time);
        var nodeB = Limiter(time);

        var allowed = 0;
        (NexaVerify.Application.Common.Error? Error, TimeSpan RetryAfter) last = default;
        for (var i = 0; i < 30; i++)
        {
            last = await (i % 2 == 0 ? nodeA : nodeB).TryAcquireAsync(clientId, Guid.NewGuid(), null, default);
            if (last.Error is null)
            {
                allowed++;
            }
        }

        allowed.ShouldBe(9);
        last.Error.ShouldNotBeNull().Code.ShouldBe(ErrorCodes.DailyQuotaExceeded);
        last.RetryAfter.ShouldBe(TimeSpan.FromHours(12) - TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1), "until the next UTC midnight");

        time.Advance(TimeSpan.FromHours(13)); // tomorrow
        (await nodeA.TryAcquireAsync(clientId, Guid.NewGuid(), null, default)).Error.ShouldBeNull();
    }

    [Fact]
    public async Task A_call_refused_by_the_daily_quota_does_not_use_up_the_minute_budget()
    {
        var clientId = await ClientWithSettingsAsync("SC3", perMinute: 3, perDay: 1);
        var time = new FakeTimeProvider(Noon);
        var node = Limiter(time);
        var credential = Guid.NewGuid();

        (await node.TryAcquireAsync(clientId, credential, null, default)).Error.ShouldBeNull();
        for (var i = 0; i < 5; i++)
        {
            (await node.TryAcquireAsync(clientId, credential, null, default)).Error.ShouldNotBeNull().Code.ShouldBe(ErrorCodes.DailyQuotaExceeded);
        }
    }

    [Fact]
    public async Task A_credential_may_lower_but_never_raise_the_accounts_limit_across_nodes()
    {
        var clientId = await ClientWithSettingsAsync("SC4", perMinute: 4, perDay: 100_000);
        var time = new FakeTimeProvider(Noon);
        var nodeA = Limiter(time);
        var nodeB = Limiter(time);
        var credential = Guid.NewGuid();

        var allowed = 0;
        for (var i = 0; i < 12; i++)
        {
            allowed += (await (i % 2 == 0 ? nodeA : nodeB).TryAcquireAsync(clientId, credential, 50_000, default)).Error is null ? 1 : 0;
        }

        allowed.ShouldBe(4);
    }

    [Fact]
    public async Task Many_concurrent_first_requests_race_on_the_same_new_bucket_without_errors_or_overshoot()
    {
        var backend = Backend();
        var key = Guid.NewGuid();

        var grants = await Task.WhenAll(Enumerable.Range(0, 60).Select(_ =>
            Task.Run(() => backend.ReserveAsync(UsageCounterKinds.Throttle, key, 77, requested: 1, limit: 25, DateTime.UtcNow, default))));

        grants.Sum().ShouldBe(25);
        grants.ShouldAllBe(g => g == 0 || g == 1);
        (await UsedAsync(UsageCounterKinds.Throttle, key, 77)).ShouldBe(25);
    }

    [Fact]
    public async Task A_block_request_is_trimmed_to_what_is_left_and_kinds_do_not_share_counters()
    {
        var backend = Backend();
        var key = Guid.NewGuid();

        (await backend.ReserveAsync(UsageCounterKinds.Day, key, 1, 8, 10, DateTime.UtcNow, default)).ShouldBe(8);
        (await backend.ReserveAsync(UsageCounterKinds.Day, key, 1, 8, 10, DateTime.UtcNow, default)).ShouldBe(2, "only 2 of the 10 were left");
        (await backend.ReserveAsync(UsageCounterKinds.Day, key, 1, 8, 10, DateTime.UtcNow, default)).ShouldBe(0);
        (await backend.ReserveAsync(UsageCounterKinds.Minute, key, 1, 8, 10, DateTime.UtcNow, default)).ShouldBe(8, "another kind with the same key is a separate counter");
        (await backend.ReserveAsync(UsageCounterKinds.Day, key, 2, 8, 10, DateTime.UtcNow, default)).ShouldBe(8, "another bucket is a separate counter");
        (await backend.ReserveAsync(UsageCounterKinds.Day, Guid.NewGuid(), 1, 50, 10, DateTime.UtcNow, default)).ShouldBe(10, "a first request above the limit is capped");
    }

    [Fact]
    public async Task Old_buckets_are_purged_and_current_ones_survive()
    {
        var backend = Backend();
        var now = DateTime.UtcNow;
        var oldMinute = Guid.NewGuid();
        var oldDay = Guid.NewGuid();
        var recentMinute = Guid.NewGuid();
        var recentDay = Guid.NewGuid();
        await backend.ReserveAsync(UsageCounterKinds.Minute, oldMinute, 1, 1, 5, now.AddHours(-3), default);
        await backend.ReserveAsync(UsageCounterKinds.Day, oldDay, 1, 1, 5, now.AddDays(-5), default);
        await backend.ReserveAsync(UsageCounterKinds.Minute, recentMinute, 1, 1, 5, now, default);
        await backend.ReserveAsync(UsageCounterKinds.Day, recentDay, 1, 1, 5, now.AddDays(-1), default); // a daily bucket from yesterday is kept for 3 days

        var removed = await new UsageCounterPurger(
            backend, Options.Create(new SharedCounterOptions()), TimeProvider.System, NullLogger<UsageCounterPurger>.Instance).PurgeOnceAsync(default);

        removed.ShouldBeGreaterThanOrEqualTo(2);
        (await UsedAsync(UsageCounterKinds.Minute, oldMinute, 1)).ShouldBe(0);
        (await UsedAsync(UsageCounterKinds.Day, oldDay, 1)).ShouldBe(0);
        (await UsedAsync(UsageCounterKinds.Minute, recentMinute, 1)).ShouldBe(1);
        (await UsedAsync(UsageCounterKinds.Day, recentDay, 1)).ShouldBe(1);
    }

    [Fact]
    public async Task Two_complete_api_hosts_on_one_database_enforce_one_per_key_limit()
    {
        if (DateTime.UtcNow.Second >= 50)
        {
            await Task.Delay(TimeSpan.FromSeconds(12)); // never straddle a minute boundary: the limit is per UTC minute
        }

        var (client, admin) = await _app.OnboardClientAsync(_platform.AccessToken, "SC6", "a@sc6.test");
        await _app.SeedLicenseAsync(client.Id, 50);
        var created = await _app.PostAsync("/api/v1/client/api-keys", new CreateApiKeyRequest("two-nodes", ["faces.read"], null, 5, null), admin.AccessToken);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var rawKey = (await created.Content.ReadFromJsonAsync<CreatedApiKeyDto>(AuthApp.Json))!.RawKey;

        await using var secondHost = new ApiFactory
        {
            ConnectionString = _app.ConnectionString,
            UseTestAuth = false,
            Settings = new Dictionary<string, string> { ["Webhooks:BackgroundEnabled"] = "false", ["Faces:Retention:Enabled"] = "false" },
        };
        using var second = secondHost.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 16; i++)
        {
            var http = i % 2 == 0 ? _app.Client : second;
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/faces/balance");
            request.Headers.Add("X-Api-Key", rawKey);
            using var response = await http.SendAsync(request);
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                response.Headers.RetryAfter.ShouldNotBeNull();
            }
        }

        statuses.Count(s => s == HttpStatusCode.OK).ShouldBe(5, string.Join(",", statuses));
        statuses.Count(s => s == HttpStatusCode.TooManyRequests).ShouldBe(11);
    }

    private async Task<long> UsedAsync(byte kind, Guid key, long bucket)
    {
        await using var connection = new SqlConnection(_app.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Used FROM api.UsageCounters WHERE Kind = @k AND KeyId = @id AND Bucket = @b";
        command.Parameters.AddWithValue("@k", kind);
        command.Parameters.AddWithValue("@id", key);
        command.Parameters.AddWithValue("@b", bucket);
        return await command.ExecuteScalarAsync() is long used ? used : 0;
    }
}
