using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Load;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>Proves the load driver (tests/Load) really exercises enroll / verify / identify end to end, with a handful of requests.</summary>
[Collection(SqlServerCollection.Name)]
public class LoadToolSmokeTests : UsageTestBase
{
    public LoadToolSmokeTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    private async Task<HttpClient> LoadClientAsync(string code, int perMinute = 100_000, string[]? scopes = null)
    {
        var t = await NewTenantAsync(code, credits: 500);
        var set = await App.PutAsync($"/api/v1/admin/clients/{t.ClientId}/settings",
            new UpdateSettingsRequest(new Dictionary<string, JsonElement> { [SettingKeys.Api.RateLimitPerMinute] = JsonSerializer.SerializeToElement(perMinute) }), Platform.AccessToken);
        set.StatusCode.ShouldBe(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());

        var created = await App.PostAsync("/api/v1/client/api-keys", new CreateApiKeyRequest("load", scopes ?? ["faces.read", "faces.enroll", "faces.verify", "faces.identify"], null, 100_000, null), t.Token);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var raw = (await created.Content.ReadFromJsonAsync<CreatedApiKeyDto>(AuthApp.Json))!.RawKey;
        var client = App.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", raw);
        return client;
    }

    [Fact]
    public async Task A_short_mixed_run_succeeds_and_reports_every_operation()
    {
        using var client = await LoadClientAsync("LD1");

        var report = await LoadRunner.RunAsync(client, new LoadOptions(Concurrency: 3, TotalRequests: 30, Profiles: 5), default);

        report.Requests.ShouldBe(30);
        report.Failed.ShouldBe(0, report.Summary());
        report.RateLimited.ShouldBe(0);
        report.Succeeded.ShouldBe(30);
        report.StatusCodes.Keys.ShouldBe([200]);
        report.Latency.Keys.ShouldContain(LoadOperation.Verify);
        report.Overall.Count.ShouldBe(30);
        report.Overall.P95Ms.ShouldBeGreaterThan(0);
        report.RequestsPerSecond.ShouldBeGreaterThan(0);
        report.Summary().ShouldContain("30 requests");
    }

    [Fact]
    public async Task The_operation_mix_decides_what_is_sent()
    {
        using var client = await LoadClientAsync("LD2");

        var report = await LoadRunner.RunAsync(client,
            new LoadOptions(Concurrency: 2, TotalRequests: 12, Profiles: 3, Mix: new Dictionary<LoadOperation, int> { [LoadOperation.Identify] = 1 }), default);

        report.Latency.Keys.ShouldBe([LoadOperation.Identify]);
        report.Succeeded.ShouldBe(12);
    }

    [Fact]
    public async Task Rate_limited_answers_are_counted_separately_from_failures()
    {
        using var client = await LoadClientAsync("LD3", perMinute: 8);

        var report = await LoadRunner.RunAsync(client,
            new LoadOptions(Concurrency: 4, TotalRequests: 40, Profiles: 2, Mix: new Dictionary<LoadOperation, int> { [LoadOperation.Verify] = 1 }), default);

        report.RateLimited.ShouldBeGreaterThan(0, report.Summary());
        report.Failed.ShouldBe(0, "429 is the limiter working, not an error");
        report.ErrorRate.ShouldBe(0);
    }

    [Fact]
    public async Task A_key_without_the_needed_scope_stops_the_run_with_a_clear_message_instead_of_hammering_the_api()
    {
        using var client = await LoadClientAsync("LD4", scopes: ["faces.read"]);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => LoadRunner.RunAsync(client, new LoadOptions(TotalRequests: 5, Profiles: 2), default));

        error.Message.ShouldContain("Could not enrol");
        error.Message.ShouldContain("403");
    }

    [Fact]
    public void Latency_percentiles_use_the_nearest_rank()
    {
        var stats = LatencyStats.From(Enumerable.Range(1, 100).Select(i => (double)i).ToList());

        (stats.Count, stats.P50Ms, stats.P95Ms, stats.P99Ms, stats.MaxMs).ShouldBe((100, 50d, 95d, 99d, 100d));
        stats.MeanMs.ShouldBe(50.5);
        LatencyStats.From([]).P95Ms.ShouldBe(0);
        LatencyStats.From([7]).P99Ms.ShouldBe(7);
    }

    [Fact]
    public async Task Bad_options_are_rejected_up_front()
    {
        using var client = new HttpClient { BaseAddress = new Uri("http://localhost/") };

        await Should.ThrowAsync<ArgumentException>(() => LoadRunner.RunAsync(client, new LoadOptions(Concurrency: 0, TotalRequests: 1), default));
        await Should.ThrowAsync<ArgumentException>(() => LoadRunner.RunAsync(client, new LoadOptions(), default));
        await Should.ThrowAsync<ArgumentException>(() => LoadRunner.RunAsync(client,
            new LoadOptions(TotalRequests: 1, Mix: new Dictionary<LoadOperation, int> { [LoadOperation.Verify] = 0 }), default));
    }
}
