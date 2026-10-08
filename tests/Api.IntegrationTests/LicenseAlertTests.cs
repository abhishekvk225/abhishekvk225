using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Application.Licensing;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Dashboards;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Domain.Api;
using NexaVerify.Domain.Licensing;
using NexaVerify.Infrastructure.Background;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>License and API-key alerts: webhook events, de-duplication across runs and nodes, isolation, and the notification feed.</summary>
[Collection(SqlServerCollection.Name)]
public class LicenseAlertTests : UsageTestBase
{
    private readonly WebhookReceiver _receiver = new();

    public LicenseAlertTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    protected override IReadOnlyDictionary<string, string>? Settings { get; } =
        new Dictionary<string, string> { ["Webhooks:AllowUnsafeTargets"] = "true", ["Webhooks:TimeoutSeconds"] = "2", ["Webhooks:BackgroundEnabled"] = "false" };

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _receiver.DisposeAsync();
    }

    private static readonly string[] AllEvents =
        [WebhookEvents.LicenseLowBalance, WebhookEvents.LicenseExpiring, WebhookEvents.LicenseExpired, WebhookEvents.LicenseExhausted, WebhookEvents.ApiKeyExpiring];

    private Task<int> RunAlertsAsync() => App.Factory.Services.GetRequiredService<LicenseAlertProcessor>().RunOnceAsync(default);

    private async Task SubscribeAsync(Tenant t) =>
        (await App.PostAsync("/api/v1/client/webhooks", new CreateWebhookRequest("alerts", _receiver.Url, AllEvents), t.Token)).StatusCode.ShouldBe(HttpStatusCode.Created);

    private Task SetConsumedAsync(Guid licenseId, int consumed) =>
        App.WithDbAsync(async db =>
        {
            await db.Licenses.Where(l => l.Id == licenseId).ExecuteUpdateAsync(s => s.SetProperty(l => l.ConsumedCredits, consumed));
            return true;
        });

    private Task<List<LicenseAlert>> AlertsAsync(Guid clientId) =>
        App.WithTenantDbAsync(clientId, db => db.LicenseAlerts.AsNoTracking().OrderBy(a => a.CreatedAt).ThenBy(a => a.AlertType).ToListAsync());

    private Task<List<WebhookDelivery>> DeliveriesAsync(Guid clientId) =>
        App.WithTenantDbAsync(clientId, db => db.WebhookDeliveries.AsNoTracking().OrderBy(d => d.Id).ToListAsync());

    /// <summary>A tenant with one license that is nearly used up and ends in 5 days, one that ended yesterday, and an API key ending in 5 days.</summary>
    private async Task<Tenant> NewAlertingTenantAsync(string code)
    {
        var t = await NewTenantAsync(code, credits: 100, licenseEnds: DateTime.UtcNow.AddDays(5));
        await SetConsumedAsync(t.LicenseId, 95);
        await App.SeedLicenseAsync(t.ClientId, 50, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddDays(-1));
        (await App.PostAsync("/api/v1/client/api-keys", new CreateApiKeyRequest("Nightly job", ["faces.read"], DateTime.UtcNow.AddDays(5), null, null), t.Token))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        await SubscribeAsync(t);
        return t;
    }

    [Fact]
    public async Task Due_alerts_are_stored_and_published_once_as_webhook_events_without_personal_data()
    {
        var t = await NewAlertingTenantAsync("A1");

        (await RunAlertsAsync()).ShouldBe(4);

        var alerts = await AlertsAsync(t.ClientId);
        alerts.Select(a => a.AlertType).Order().ShouldBe(new[] { LicenseAlertType.LowBalance, LicenseAlertType.Expiring, LicenseAlertType.Expired, LicenseAlertType.ApiKeyExpiring }.Order());
        var deliveries = await DeliveriesAsync(t.ClientId);
        deliveries.Select(d => d.EventType).Order(StringComparer.Ordinal).ShouldBe(
            [WebhookEvents.ApiKeyExpiring, WebhookEvents.LicenseExpired, WebhookEvents.LicenseExpiring, WebhookEvents.LicenseLowBalance]);
        foreach (var d in deliveries)
        {
            d.PayloadJson.ShouldNotContain("NXV-"); // no license key
            d.PayloadJson.ShouldNotContain("a@a1.test"); // no user data
            System.Text.RegularExpressions.Regex.IsMatch(d.PayloadJson, "nxv_live_[A-Za-z0-9_-]{30,}").ShouldBeFalse(); // the public prefix may appear, a full key never
        }

        // they really reach the receiver, signed and in the standard envelope
        await App.Factory.Services.GetRequiredService<WebhookDispatcher>().DispatchDueAsync(default);
        _receiver.Requests.Count.ShouldBe(4);
        var low = _receiver.Requests.Single(r => r.Body.Contains("\"type\":\"license.low_balance\"", StringComparison.Ordinal));
        low.Body.ShouldContain("\"remainingCredits\":5");
        low.Body.ShouldContain("\"percentRemaining\":5");
    }

    [Fact]
    public async Task Running_again_or_after_a_restart_does_not_send_anything_twice()
    {
        var t = await NewAlertingTenantAsync("A2");
        (await RunAlertsAsync()).ShouldBe(4);

        (await RunAlertsAsync()).ShouldBe(0);
        // a "restart": a brand-new processor instance has no memory, only the database
        var fresh = ActivatorUtilities.CreateInstance<LicenseAlertProcessor>(App.Factory.Services);
        (await fresh.RunOnceAsync(default)).ShouldBe(0);

        (await AlertsAsync(t.ClientId)).Count.ShouldBe(4);
        (await DeliveriesAsync(t.ClientId)).Count.ShouldBe(4);
    }

    [Fact]
    public async Task Overlapping_runs_on_two_nodes_raise_each_alert_exactly_once()
    {
        var t = await NewAlertingTenantAsync("A3");
        var second = ActivatorUtilities.CreateInstance<LicenseAlertProcessor>(App.Factory.Services);

        var raised = await Task.WhenAll(RunAlertsAsync(), second.RunOnceAsync(default), RunAlertsAsync());

        raised.Sum().ShouldBe(4);
        (await AlertsAsync(t.ClientId)).Count.ShouldBe(4);
        (await DeliveriesAsync(t.ClientId)).Count.ShouldBe(4); // the loser's webhook rows rolled back with its alert row
    }

    [Fact]
    public async Task Crossing_a_new_threshold_alerts_again_but_the_same_one_never_does()
    {
        var t = await NewAlertingTenantAsync("A4");
        (await RunAlertsAsync()).ShouldBe(4);

        await SetConsumedAsync(t.LicenseId, 100); // used up: exhausted is new, low was already sent
        (await RunAlertsAsync()).ShouldBe(1);
        (await AlertsAsync(t.ClientId)).ShouldContain(a => a.AlertType == LicenseAlertType.Exhausted);
        (await DeliveriesAsync(t.ClientId)).ShouldContain(d => d.EventType == WebhookEvents.LicenseExhausted);

        // topped up (the total changes) and then running low again is a new crossing
        (await App.PostAsync($"/api/v1/admin/licenses/{t.LicenseId}/adjust", new AdjustLicenseRequest(100, "top-up"), Platform.AccessToken)).EnsureSuccessStatusCode();
        (await RunAlertsAsync()).ShouldBe(0);
        await SetConsumedAsync(t.LicenseId, 195);
        (await RunAlertsAsync()).ShouldBe(1);
        (await AlertsAsync(t.ClientId)).Count(a => a.AlertType == LicenseAlertType.LowBalance).ShouldBe(2);
    }

    [Fact]
    public async Task A_license_the_sweeper_has_already_expired_is_not_announced_a_second_time()
    {
        var t = await NewAlertingTenantAsync("A5");
        (await RunAlertsAsync()).ShouldBe(4);

        await App.WithServicesAsync(null, sp => sp.GetRequiredService<ILicenseExpiryProcessor>().ProcessAsync(10, default));

        (await RunAlertsAsync()).ShouldBe(0);
        (await AlertsAsync(t.ClientId)).Count(a => a.AlertType == LicenseAlertType.Expired).ShouldBe(1);
    }

    [Fact]
    public async Task Healthy_clients_get_nothing_and_suspended_clients_are_skipped()
    {
        var healthy = await NewTenantAsync("A6", credits: 100);
        var suspended = await NewAlertingTenantAsync("A6S");
        (await App.PostAsync($"/api/v1/admin/clients/{suspended.ClientId}/suspend", new NexaVerify.Contracts.Tenancy.ClientStatusRequest("non-payment"), Platform.AccessToken)).EnsureSuccessStatusCode();

        (await RunAlertsAsync()).ShouldBe(0);

        (await AlertsAsync(healthy.ClientId)).ShouldBeEmpty();
        (await AlertsAsync(suspended.ClientId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_notification_feed_lists_the_clients_own_alerts_and_supports_mark_read()
    {
        var a = await NewAlertingTenantAsync("A7");
        var b = await NewTenantAsync("A7B", credits: 100);
        await RunAlertsAsync();

        var feed = await GetAsync<NotificationFeedDto>("/api/v1/client/notifications", a.Token);

        feed.UnreadCount.ShouldBe(4);
        feed.Notifications.TotalCount.ShouldBe(4);
        feed.Notifications.Items.ShouldAllBe(n => !n.IsRead && n.Title.Length > 0 && n.Message.Length > 0 && n.CreatedAt > DateTime.UtcNow.AddMinutes(-5));
        feed.Notifications.Items.Select(n => n.Type).Order(StringComparer.Ordinal).ShouldBe(["ApiKeyExpiring", "Expired", "Expiring", "LowBalance"]);
        feed.Notifications.Items.Single(n => n.Type == "LowBalance").Severity.ShouldBe("Warning");
        feed.Notifications.Items.Single(n => n.Type == "Expired").Severity.ShouldBe("Critical");

        var first = feed.Notifications.Items[0];
        (await App.PostAsync($"/api/v1/client/notifications/{first.Id}/read", null, a.Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await App.PostAsync($"/api/v1/client/notifications/{first.Id}/read", null, a.Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent); // idempotent
        (await GetAsync<NotificationFeedDto>("/api/v1/client/notifications", a.Token)).UnreadCount.ShouldBe(3);
        (await GetAsync<NotificationFeedDto>("/api/v1/client/notifications?unreadOnly=true", a.Token)).Notifications.Items.Count.ShouldBe(3);

        // another client sees none of it and cannot touch it
        var other = await GetAsync<NotificationFeedDto>("/api/v1/client/notifications", b.Token);
        other.UnreadCount.ShouldBe(0);
        other.Notifications.Items.ShouldBeEmpty();
        (await App.PostAsync($"/api/v1/client/notifications/{first.Id}/read", null, b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_notification_feed_requires_a_client_credential()
    {
        (await App.GetAsync("/api/v1/client/notifications")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await App.GetAsync("/api/v1/client/notifications", Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await App.PostAsync($"/api/v1/client/notifications/{Guid.NewGuid()}/read", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
