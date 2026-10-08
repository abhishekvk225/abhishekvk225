using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Common;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>Per-principal throttles on dashboards, CSV exports and the webhook test/retry actions (shared counters, 429 + Retry-After).</summary>
[Collection(SqlServerCollection.Name)]
public class ThrottleTests : UsageTestBase
{
    private const string ClientDashboard = "/api/v1/client/dashboard";
    private const string AdminDashboard = "/api/v1/admin/dashboard";

    public ThrottleTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    protected override IReadOnlyDictionary<string, string>? Settings => new Dictionary<string, string>
    {
        ["Throttle:DashboardsPerMinute"] = "3",
        ["Throttle:ExportsPerMinute"] = "2",
        ["Throttle:WebhookTestsPerMinute"] = "2",
        ["Throttle:WebhookRetriesPerMinute"] = "2",
        ["Webhooks:AllowUnsafeTargets"] = "true",
        ["Webhooks:BackgroundEnabled"] = "false",
    };

    private static async Task AvoidMinuteBoundaryAsync()
    {
        if (DateTime.UtcNow.Second >= 54)
        {
            await Task.Delay(TimeSpan.FromSeconds(8)); // the windows are UTC minutes
        }
    }

    private static async Task<string?> CodeOf(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>(AuthApp.Json)).GetProperty("code").GetString();

    private async Task AssertThrottledAsync(Func<Task<HttpResponseMessage>> call, int allowed)
    {
        for (var i = 0; i < allowed; i++)
        {
            var ok = await call();
            ok.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests, $"call {i + 1} of {allowed} must pass");
        }

        var limited = await call();
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBeInRange(1, 60);
        (await CodeOf(limited)).ShouldBe(ErrorCodes.RateLimited);
    }

    [Fact]
    public async Task The_client_dashboard_is_throttled_per_user_and_another_client_is_unaffected()
    {
        await AvoidMinuteBoundaryAsync();
        var a = await NewTenantAsync("T1A");
        var b = await NewTenantAsync("T1B");

        await AssertThrottledAsync(() => App.GetAsync(ClientDashboard, a.Token), allowed: 3);

        (await App.GetAsync(ClientDashboard, b.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_admin_dashboard_is_throttled_per_staff_member()
    {
        await AvoidMinuteBoundaryAsync();

        await AssertThrottledAsync(() => App.GetAsync(AdminDashboard, Platform.AccessToken), allowed: 3);
    }

    [Fact]
    public async Task Unauthenticated_and_forbidden_calls_never_reach_the_counters()
    {
        await AvoidMinuteBoundaryAsync();
        var a = await NewTenantAsync("T2");

        for (var i = 0; i < 6; i++)
        {
            (await App.GetAsync(ClientDashboard)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await App.GetAsync(AdminDashboard, a.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // the real budget is untouched
        (await App.GetAsync(ClientDashboard, a.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Csv_exports_have_their_own_tighter_budget_per_principal()
    {
        await AvoidMinuteBoundaryAsync();
        var a = await NewTenantAsync("T3");
        var b = await NewTenantAsync("T3B");
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        await AssertThrottledAsync(() => App.GetAsync($"/api/v1/client/reports/usage.csv?from={today}&to={today}", a.Token), allowed: 2);

        (await App.GetAsync($"/api/v1/client/reports/usage.csv?from={today}&to={today}", b.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await App.GetAsync(ClientDashboard, a.Token)).StatusCode.ShouldBe(HttpStatusCode.OK); // dashboards are a separate policy
    }

    [Fact]
    public async Task The_admin_export_is_throttled_and_a_throttled_export_is_not_audited()
    {
        await AvoidMinuteBoundaryAsync();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        await AssertThrottledAsync(() => App.GetAsync($"/api/v1/admin/reports/usage.csv?from={today}&to={today}", Platform.AccessToken), allowed: 2);

        (await App.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == "report.exported"))).ShouldBe(2);
    }

    [Fact]
    public async Task Sending_test_events_is_throttled_and_only_the_allowed_ones_are_queued()
    {
        await AvoidMinuteBoundaryAsync();
        var t = await NewTenantAsync("T4");
        await using var receiver = new WebhookReceiver();
        var created = await App.PostAsync("/api/v1/client/webhooks", new CreateWebhookRequest("ci", receiver.Url, ["recognition.completed"]), t.Token);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var hook = (await created.Content.ReadFromJsonAsync<CreatedWebhookDto>(AuthApp.Json))!;

        await AssertThrottledAsync(() => App.PostAsync($"/api/v1/client/webhooks/{hook.Endpoint.Id}/test", null, t.Token), allowed: 2);

        (await App.WithDbAsync(db => db.WebhookDeliveries.CountAsync())).ShouldBe(2);
    }

    [Fact]
    public async Task Retrying_deliveries_is_throttled_even_when_the_delivery_does_not_exist()
    {
        await AvoidMinuteBoundaryAsync();
        var t = await NewTenantAsync("T5");
        await using var receiver = new WebhookReceiver();
        var created = await App.PostAsync("/api/v1/client/webhooks", new CreateWebhookRequest("ci", receiver.Url, ["recognition.completed"]), t.Token);
        var hook = (await created.Content.ReadFromJsonAsync<CreatedWebhookDto>(AuthApp.Json))!;

        for (var i = 0; i < 2; i++)
        {
            (await App.PostAsync($"/api/v1/client/webhooks/{hook.Endpoint.Id}/deliveries/99999/retry", null, t.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        (await App.PostAsync($"/api/v1/client/webhooks/{hook.Endpoint.Id}/deliveries/99999/retry", null, t.Token)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Webhook_actions_of_another_client_are_still_a_404_and_cost_the_caller_budget_only()
    {
        await AvoidMinuteBoundaryAsync();
        var a = await NewTenantAsync("T6A");
        var b = await NewTenantAsync("T6B");
        await using var receiver = new WebhookReceiver();
        var created = await App.PostAsync("/api/v1/client/webhooks", new CreateWebhookRequest("ci", receiver.Url, ["recognition.completed"]), a.Token);
        var hook = (await created.Content.ReadFromJsonAsync<CreatedWebhookDto>(AuthApp.Json))!;

        (await App.PostAsync($"/api/v1/client/webhooks/{hook.Endpoint.Id}/test", null, b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await App.PostAsync($"/api/v1/client/webhooks/{hook.Endpoint.Id}/test", null, a.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
