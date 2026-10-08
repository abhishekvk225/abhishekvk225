using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Api;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Identity;
using NexaVerify.Domain.Api;
using NexaVerify.Infrastructure.Background;
using NexaVerify.Infrastructure.Platform;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>Webhook dispatcher scheduling: per-endpoint fairness, configurable batch/lease, exclusive claims across nodes, failed EVENTS (not attempts).</summary>
[Collection(SqlServerCollection.Name)]
public class WebhookDispatchFairnessTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private AuthApp _app = null!;
    private LoginResponse _platform = null!;
    private WebhookReceiver _receiver = null!;

    public WebhookDispatchFairnessTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _receiver = new WebhookReceiver();
        _app = await AuthApp.CreateAsync(_fixture, new Dictionary<string, string>
        {
            ["Webhooks:AllowUnsafeTargets"] = "true",
            ["Webhooks:TimeoutSeconds"] = "1",
            ["Webhooks:BackgroundEnabled"] = "false",
            ["Webhooks:BatchSize"] = "12",
            ["Webhooks:Parallelism"] = "4",
            ["Webhooks:MaxPerEndpointPerCycle"] = "3",
            ["Webhooks:DisableAfterFailedEvents"] = "3",
        });
        _platform = await _app.SuperAdminAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _receiver.DisposeAsync();
    }

    private async Task<(Guid ClientId, string Token)> NewTenantAsync(string code)
    {
        var (client, admin) = await _app.OnboardClientAsync(_platform.AccessToken, code, $"a@{code.ToLowerInvariant()}.test");
        return (client.Id, admin.AccessToken);
    }

    private async Task<Guid> NewEndpointAsync(string token, string name)
    {
        var response = await _app.PostAsync("/api/v1/client/webhooks", new CreateWebhookRequest(name, _receiver.Url, ["recognition.completed"]), token);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CreatedWebhookDto>(AuthApp.Json))!.Endpoint.Id;
    }

    private Task QueueAsync(Guid clientId, Guid endpointId, int count, DateTime? at = null) => _app.WithDbAsync(async db =>
    {
        var due = at ?? DateTime.UtcNow.AddMinutes(-5);
        for (var i = 0; i < count; i++)
        {
            var eventId = Guid.NewGuid();
            db.WebhookDeliveries.Add(WebhookDelivery.Queue(clientId, endpointId, eventId, "recognition.completed",
                WebhookSigning.Envelope(eventId, "recognition.completed", due, new { n = i }), due));
        }

        await db.SaveChangesAsync();
        return true;
    });

    private Task<int> DispatchAsync() => _app.Factory.Services.GetRequiredService<WebhookDispatcher>().DispatchDueAsync(default);

    private Task<List<WebhookDelivery>> DeliveriesAsync() => _app.WithDbAsync(db => db.WebhookDeliveries.AsNoTracking().OrderBy(d => d.Id).ToListAsync());

    private Task MakeDueAsync() => _app.WithDbAsync(async db =>
    {
        await db.WebhookDeliveries.Where(d => d.Status == DeliveryStatus.Pending).ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
        return true;
    });

    private Task<WebhookEndpoint> EndpointAsync(Guid id) => _app.WithDbAsync(db => db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Id == id));

    [Fact]
    public async Task A_huge_backlog_on_one_endpoint_does_not_starve_the_others()
    {
        var (clientId, token) = await NewTenantAsync("F1");
        var busy = await NewEndpointAsync(token, "busy");
        var quietB = await NewEndpointAsync(token, "quiet-b");
        var quietC = await NewEndpointAsync(token, "quiet-c");
        await QueueAsync(clientId, busy, 30, DateTime.UtcNow.AddHours(-2)); // oldest by far: a plain FIFO batch of 12 would be all "busy"
        await QueueAsync(clientId, quietB, 2);
        await QueueAsync(clientId, quietC, 2);

        var sent = await DispatchAsync();

        sent.ShouldBe(3 + 2 + 2);
        var delivered = (await DeliveriesAsync()).Where(d => d.Status == DeliveryStatus.Delivered).GroupBy(d => d.EndpointId).ToDictionary(g => g.Key, g => g.Count());
        delivered[busy].ShouldBe(3);
        delivered[quietB].ShouldBe(2);
        delivered[quietC].ShouldBe(2);
        _receiver.Requests.Count.ShouldBe(7);
    }

    [Fact]
    public async Task A_busy_endpoint_is_throttled_to_a_few_deliveries_per_cycle_and_still_drains_over_time()
    {
        var (clientId, token) = await NewTenantAsync("F2");
        var busy = await NewEndpointAsync(token, "busy");
        await QueueAsync(clientId, busy, 10);

        (await DispatchAsync()).ShouldBe(3);
        (await DispatchAsync()).ShouldBe(3);
        (await DeliveriesAsync()).Count(d => d.Status == DeliveryStatus.Delivered).ShouldBe(6);

        while (await DispatchAsync() > 0)
        {
        }

        (await DeliveriesAsync()).ShouldAllBe(d => d.Status == DeliveryStatus.Delivered);
    }

    [Fact]
    public async Task The_batch_size_is_configurable_and_caps_one_cycle_across_endpoints()
    {
        var (clientId, token) = await NewTenantAsync("F3");
        for (var i = 0; i < 6; i++)
        {
            await QueueAsync(clientId, await NewEndpointAsync(token, $"e{i}"), 5);
        }

        (await DispatchAsync()).ShouldBe(12); // 6 endpoints x 3 would be 18, the batch is 12
    }

    [Fact]
    public async Task Concurrent_claims_by_several_nodes_never_hand_out_the_same_delivery_twice()
    {
        var (clientId, token) = await NewTenantAsync("F4");
        for (var i = 0; i < 6; i++)
        {
            await QueueAsync(clientId, await NewEndpointAsync(token, $"e{i}"), 10);
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            await _app.WithServicesAsync(null, sp => sp.GetRequiredService<WebhookStore>()
                .ClaimDueAsync(batch: 7, perEndpoint: 100, DateTime.UtcNow, TimeSpan.FromMinutes(5), default)))));

        var all = claims.SelectMany(c => c).ToList();
        all.Count.ShouldBe(all.Distinct().Count(), "a delivery must be claimed by exactly one node");
        all.Count.ShouldBeGreaterThan(7, "several claims got work");
        all.Count.ShouldBeLessThanOrEqualTo(56);
    }

    [Fact]
    public async Task A_claimed_delivery_is_leased_and_not_handed_out_again_until_the_lease_ends()
    {
        var (clientId, token) = await NewTenantAsync("F5");
        await QueueAsync(clientId, await NewEndpointAsync(token, "e"), 2);

        var first = await _app.WithServicesAsync(null, sp => sp.GetRequiredService<WebhookStore>().ClaimDueAsync(10, 10, DateTime.UtcNow, TimeSpan.FromMinutes(10), default));
        var second = await _app.WithServicesAsync(null, sp => sp.GetRequiredService<WebhookStore>().ClaimDueAsync(10, 10, DateTime.UtcNow, TimeSpan.FromMinutes(10), default));
        var afterLease = await _app.WithServicesAsync(null, sp => sp.GetRequiredService<WebhookStore>().ClaimDueAsync(10, 10, DateTime.UtcNow.AddMinutes(11), TimeSpan.FromMinutes(10), default));

        first.Count.ShouldBe(2);
        second.ShouldBeEmpty();
        afterLease.Count.ShouldBe(2, "a crashed node's work reappears after the lease");
    }

    [Fact]
    public async Task One_event_that_is_retried_many_times_counts_as_one_failure_not_many()
    {
        var (clientId, token) = await NewTenantAsync("F6");
        var hook = await NewEndpointAsync(token, "flaky");
        _receiver.StatusCode = 500;
        await QueueAsync(clientId, hook, 1);

        for (var attempt = 0; attempt < WebhookDelivery.MaxAttempts; attempt++)
        {
            await MakeDueAsync();
            await DispatchAsync();
        }

        (await DeliveriesAsync()).Single().Status.ShouldBe(DeliveryStatus.Abandoned);
        var endpoint = await EndpointAsync(hook);
        endpoint.FailureCount.ShouldBe(1, "eight attempts at the same event are one failed event");
        endpoint.Status.ShouldBe(WebhookStatus.Active);
    }

    [Fact]
    public async Task The_endpoint_is_switched_off_after_the_configured_number_of_consecutive_failed_events()
    {
        var (clientId, token) = await NewTenantAsync("F7");
        var hook = await NewEndpointAsync(token, "down");
        _receiver.StatusCode = 503;
        await QueueAsync(clientId, hook, 3);

        await DispatchAsync();

        var endpoint = await EndpointAsync(hook);
        endpoint.Status.ShouldBe(WebhookStatus.Disabled);
        endpoint.FailureCount.ShouldBe(3);
        endpoint.DisabledReason.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_success_in_between_resets_the_run_of_failed_events()
    {
        var (clientId, token) = await NewTenantAsync("F8");
        var hook = await NewEndpointAsync(token, "mostly-up");
        _receiver.StatusCode = 500;
        await QueueAsync(clientId, hook, 2);
        await DispatchAsync();
        (await EndpointAsync(hook)).FailureCount.ShouldBe(2);

        _receiver.StatusCode = 200;
        await QueueAsync(clientId, hook, 1);
        await DispatchAsync();

        (await EndpointAsync(hook)).FailureCount.ShouldBe(0);
        (await EndpointAsync(hook)).Status.ShouldBe(WebhookStatus.Active);
    }

    [Fact]
    public async Task A_test_event_that_fails_never_counts_toward_switching_the_endpoint_off()
    {
        var (_, token) = await NewTenantAsync("F9");
        var hook = await NewEndpointAsync(token, "tested");
        _receiver.StatusCode = 500;

        for (var i = 0; i < 5; i++)
        {
            (await _app.PostAsync($"/api/v1/client/webhooks/{hook}/test", null, token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await DispatchAsync();
        await DispatchAsync();

        var endpoint = await EndpointAsync(hook);
        (endpoint.FailureCount, endpoint.Status).ShouldBe((0, WebhookStatus.Active));
    }
}
