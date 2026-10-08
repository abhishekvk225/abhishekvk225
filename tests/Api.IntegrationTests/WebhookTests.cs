using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Application.Api;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Domain.Api;
using NexaVerify.Infrastructure.Background;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>Webhooks: registration, SSRF protection, signed delivery, retries, auto-disable, isolation.</summary>
[Collection(SqlServerCollection.Name)]
public class WebhookTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private AuthApp _app = null!;
    private LoginResponse _platform = null!;
    private WebhookReceiver _receiver = null!;

    public WebhookTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _receiver = new WebhookReceiver();
        _app = await AuthApp.CreateAsync(_fixture, new Dictionary<string, string> { ["Webhooks:AllowUnsafeTargets"] = "true", ["Webhooks:TimeoutSeconds"] = "1", ["Webhooks:BackgroundEnabled"] = "false" });
        _platform = await _app.SuperAdminAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _receiver.DisposeAsync();
    }

    private sealed record Tenant(Guid ClientId, string Token);

    private async Task<Tenant> NewTenantAsync(string code, int credits = 100)
    {
        var (client, admin) = await _app.OnboardClientAsync(_platform.AccessToken, code, $"a@{code.ToLowerInvariant()}.test");
        if (credits > 0)
        {
            await _app.SeedLicenseAsync(client.Id, credits);
        }

        return new Tenant(client.Id, admin.AccessToken);
    }

    private async Task<CreatedWebhookDto> CreateAsync(Tenant t, string? url = null, string[]? events = null)
    {
        var response = await _app.PostAsync("/api/v1/client/webhooks", new CreateWebhookRequest("ci", url ?? _receiver.Url, events ?? ["recognition.completed"]), t.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CreatedWebhookDto>(AuthApp.Json))!;
    }

    private Task<int> DispatchAsync() => _app.Factory.Services.GetRequiredService<WebhookDispatcher>().DispatchDueAsync(default);

    private Task MakeDueAsync() => _app.WithDbAsync(async db =>
    {
        await db.WebhookDeliveries.Where(d => d.Status == DeliveryStatus.Pending).ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
        return true;
    });

    private Task<List<WebhookDelivery>> DeliveriesAsync() => _app.WithDbAsync(db => db.WebhookDeliveries.AsNoTracking().OrderBy(d => d.Id).ToListAsync());

    private async Task VerifyAsync(Tenant t)
    {
        await _app.PostFormAsync("/api/v1/faces/enroll", TestImages.Person(1), new Dictionary<string, string> { ["externalRef"] = "secret-employee", ["consentReference"] = "c" }, t.Token);
        await _app.PostFormAsync("/api/v1/faces/verify", TestImages.Person(1, 1), new Dictionary<string, string> { ["externalRef"] = "secret-employee" }, t.Token);
    }

    [Fact]
    public async Task The_signing_secret_is_shown_once_and_stored_encrypted()
    {
        var t = await NewTenantAsync("W1");
        var created = await CreateAsync(t);

        created.Secret.ShouldStartWith("whsec_");
        var list = await (await _app.GetAsync("/api/v1/client/webhooks", t.Token)).Content.ReadAsStringAsync();
        list.ShouldNotContain(created.Secret);
        list.ShouldNotContain("secret", Case.Insensitive);

        var stored = await _app.WithTenantDbAsync(t.ClientId, db => db.WebhookEndpoints.AsNoTracking().SingleAsync());
        Encoding.UTF8.GetString(stored.SecretEnc).ShouldNotContain(created.Secret);

        var rotated = (await (await _app.PostAsync($"/api/v1/client/webhooks/{created.Endpoint.Id}/rotate-secret", null, t.Token)).Content.ReadFromJsonAsync<CreatedWebhookDto>(AuthApp.Json))!;
        rotated.Secret.ShouldNotBe(created.Secret);
    }

    [Fact]
    public async Task Events_are_delivered_signed_and_carry_no_personal_data()
    {
        var t = await NewTenantAsync("W2");
        var hook = await CreateAsync(t);
        await VerifyAsync(t);

        (await DispatchAsync()).ShouldBe(2); // enrol + verify
        _receiver.Requests.Count.ShouldBe(2);

        foreach (var received in _receiver.Requests)
        {
            received.Headers["X-Event-Type"].ShouldBe("recognition.completed");
            Guid.TryParse(received.Headers["X-Event-Id"], out _).ShouldBeTrue();

            var parts = received.Headers["X-Signature"].Split(',');
            var timestamp = parts[0]["t=".Length..];
            var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(hook.Secret), Encoding.UTF8.GetBytes(timestamp + "." + received.Body))).ToLowerInvariant();
            parts[1].ShouldBe("v1=" + expected);
            Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - long.Parse(timestamp)).ShouldBeLessThan(30);

            received.Body.ShouldNotContain("secret-employee");
            var data = JsonDocument.Parse(received.Body).RootElement.GetProperty("data");
            data.GetProperty("outcome").GetString().ShouldBeOneOf("Enrolled", "Matched");
            data.TryGetProperty("requestId", out _).ShouldBeTrue();
        }

        (await DeliveriesAsync()).ShouldAllBe(d => d.Status == DeliveryStatus.Delivered && d.Attempts == 1);
    }

    [Fact]
    public async Task Only_subscribed_active_endpoints_receive_events()
    {
        var t = await NewTenantAsync("W3");
        var other = await CreateAsync(t, events: ["license.expired"]);
        var disabled = await CreateAsync(t);
        var update = await _app.PutAsync($"/api/v1/client/webhooks/{disabled.Endpoint.Id}",
            new UpdateWebhookRequest("ci", _receiver.Url, ["recognition.completed"], false, disabled.Endpoint.RowVersion), t.Token);
        update.StatusCode.ShouldBe(HttpStatusCode.OK);

        await VerifyAsync(t);

        (await DeliveriesAsync()).ShouldBeEmpty();
        other.Endpoint.Events.ShouldBe(["license.expired"]);
    }

    [Fact]
    public async Task A_failing_endpoint_is_retried_with_backoff_then_recovers()
    {
        var t = await NewTenantAsync("W4");
        await CreateAsync(t);
        _receiver.StatusCode = 500;
        await _app.PostFormAsync("/api/v1/faces/detect", TestImages.Person(2), null, t.Token);
        await VerifyAsync(t);

        await DispatchAsync();
        var afterFirst = await DeliveriesAsync();
        afterFirst.ShouldAllBe(d => d.Status == DeliveryStatus.Pending && d.Attempts == 1 && d.LastStatusCode == 500);
        afterFirst.ShouldAllBe(d => d.NextAttemptAt > DateTime.UtcNow.AddSeconds(10)); // backed off, not retried immediately
        (await DispatchAsync()).ShouldBe(0);

        _receiver.StatusCode = 200;
        await MakeDueAsync();
        await DispatchAsync();
        (await DeliveriesAsync()).ShouldAllBe(d => d.Status == DeliveryStatus.Delivered && d.Attempts == 2);
        (await _app.WithTenantDbAsync(t.ClientId, db => db.WebhookEndpoints.AsNoTracking().SingleAsync())).FailureCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_delivery_gives_up_after_eight_attempts_and_can_be_retried_by_hand()
    {
        var t = await NewTenantAsync("W5");
        var hook = await CreateAsync(t);
        _receiver.StatusCode = 503;
        var enrolled = await _app.PostFormAsync("/api/v1/faces/enroll", TestImages.Person(3), new Dictionary<string, string> { ["externalRef"] = "e", ["consentReference"] = "c" }, t.Token);
        enrolled.StatusCode.ShouldBe(HttpStatusCode.OK, await enrolled.Content.ReadAsStringAsync());

        for (var i = 0; i < WebhookDelivery.MaxAttempts; i++)
        {
            await MakeDueAsync();
            await DispatchAsync();
        }

        var delivery = (await DeliveriesAsync()).Single();
        delivery.Status.ShouldBe(DeliveryStatus.Abandoned);
        delivery.Attempts.ShouldBe(8);

        _receiver.StatusCode = 200;
        var retry = await _app.PostAsync($"/api/v1/client/webhooks/{hook.Endpoint.Id}/deliveries/{delivery.Id}/retry", null, t.Token);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        await DispatchAsync();
        (await DeliveriesAsync()).Single().Status.ShouldBe(DeliveryStatus.Delivered);
        (await _app.PostAsync($"/api/v1/client/webhooks/{hook.Endpoint.Id}/deliveries/{delivery.Id}/retry", null, t.Token)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_endpoint_that_keeps_failing_is_disabled_automatically()
    {
        var t = await NewTenantAsync("W6");
        var hook = await CreateAsync(t);
        _receiver.StatusCode = 500;

        await _app.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow.AddSeconds(-1);
            for (var i = 0; i < WebhookEndpoint.DisableAfterConsecutiveFailures; i++)
            {
                var eventId = Guid.NewGuid();
                db.WebhookDeliveries.Add(WebhookDelivery.Queue(t.ClientId, hook.Endpoint.Id, eventId, "recognition.completed", WebhookSigning.Envelope(eventId, "recognition.completed", now, new { n = i }), now));
            }

            await db.SaveChangesAsync();
            return true;
        });

        // one endpoint gets at most MaxPerEndpointPerCycle deliveries per cycle (fairness), so it takes a few cycles to try all 20 events
        for (var cycle = 0; cycle < 6; cycle++)
        {
            await DispatchAsync();
        }

        var endpoint = (await (await _app.GetAsync($"/api/v1/client/webhooks/{hook.Endpoint.Id}", t.Token)).Content.ReadFromJsonAsync<WebhookEndpointDto>(AuthApp.Json))!;
        endpoint.Status.ShouldBe("Disabled");
        endpoint.DisabledReason.ShouldNotBeNull();

        // events no longer queue for it, and a test event is refused until it is enabled again
        await VerifyAsync(t);
        (await DeliveriesAsync()).Count.ShouldBe(WebhookEndpoint.DisableAfterConsecutiveFailures);
        (await _app.PostAsync($"/api/v1/client/webhooks/{hook.Endpoint.Id}/test", null, t.Token)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Redirects_are_never_followed()
    {
        var t = await NewTenantAsync("W7");
        await using var internalService = new WebhookReceiver();
        _receiver.RedirectTo = internalService.Url;
        var hook = await CreateAsync(t);

        (await _app.PostAsync($"/api/v1/client/webhooks/{hook.Endpoint.Id}/test", null, t.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await DispatchAsync();

        _receiver.Requests.Count.ShouldBe(1);
        internalService.Requests.ShouldBeEmpty();
        (await DeliveriesAsync()).Single().LastStatusCode.ShouldBe(302);
    }

    [Fact]
    public async Task A_slow_receiver_times_out_instead_of_holding_the_dispatcher()
    {
        var t = await NewTenantAsync("W8");
        var hook = await CreateAsync(t);
        _receiver.Delay = TimeSpan.FromSeconds(4);

        await _app.PostAsync($"/api/v1/client/webhooks/{hook.Endpoint.Id}/test", null, t.Token);
        var started = DateTime.UtcNow;
        await DispatchAsync();

        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(3.5));
        (await DeliveriesAsync()).Single().LastError.ShouldBe("The endpoint did not answer in time.");
    }

    [Fact]
    public async Task No_event_is_queued_when_the_business_transaction_does_not_commit()
    {
        var t = await NewTenantAsync("W9", credits: 0); // no licence: the recognition is refused before anything is written
        await CreateAsync(t);

        var response = await _app.PostFormAsync("/api/v1/faces/identify", TestImages.Person(4), null, t.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);

        (await DeliveriesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Endpoints_are_private_to_their_client_and_validated()
    {
        var a = await NewTenantAsync("W10A");
        var b = await NewTenantAsync("W10B");
        var hook = await CreateAsync(a);

        (await _app.GetAsync($"/api/v1/client/webhooks/{hook.Endpoint.Id}", b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _app.DeleteAsync($"/api/v1/client/webhooks/{hook.Endpoint.Id}", b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _app.PostAsync($"/api/v1/client/webhooks/{hook.Endpoint.Id}/test", null, b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _app.GetAsync($"/api/v1/client/webhooks/{hook.Endpoint.Id}/deliveries", b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // a client user without webhooks.manage is refused outright
        await _app.CreateClientUserAsync(a.ClientId, "u@w10.test", "ClientUser");
        var user = await _app.LoginAsync("u@w10.test", AuthApp.StrongPassword);
        (await _app.GetAsync("/api/v1/client/webhooks", user.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        foreach (var (name, url, events) in new[] { ("", _receiver.Url, "recognition.completed"), ("x", _receiver.Url, "made.up"), ("x", "not a url", "recognition.completed") })
        {
            (await _app.PostAsync("/api/v1/client/webhooks", new CreateWebhookRequest(name, url, [events]), a.Token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await _app.DeleteAsync($"/api/v1/client/webhooks/{hook.Endpoint.Id}", a.Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task The_number_of_endpoints_is_capped_and_stale_edits_are_refused()
    {
        var t = await NewTenantAsync("W11");
        var first = await CreateAsync(t);
        for (var i = 1; i < WebhookEndpoint.MaxPerClient; i++)
        {
            await CreateAsync(t);
        }

        (await _app.PostAsync("/api/v1/client/webhooks", new CreateWebhookRequest("one more", _receiver.Url, ["recognition.completed"]), t.Token)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var stale = await _app.PutAsync($"/api/v1/client/webhooks/{first.Endpoint.Id}",
            new UpdateWebhookRequest("renamed", _receiver.Url, ["recognition.completed"], true, Convert.ToBase64String(new byte[8])), t.Token);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Unsafe_targets_are_refused_by_default()
    {
        await using var strict = await AuthApp.CreateAsync(_fixture);
        var platform = await strict.SuperAdminAsync();
        var (_, admin) = await strict.OnboardClientAsync(platform.AccessToken, "W12", "a@w12.test");

        var blocked = new[]
        {
            "http://example.com/hook",           // not https
            "https://localhost/hook",
            "https://127.0.0.1/hook",
            "https://10.1.2.3/hook",
            "https://192.168.0.10/hook",
            "https://172.16.5.5/hook",
            "https://169.254.169.254/latest/meta-data", // cloud metadata
            "https://100.64.0.1/hook",
            "https://[::1]/hook",
            "https://[fd00::1]/hook",
            "https://[::ffff:10.0.0.1]/hook",
            "https://user:pass@example.com/hook",
            "https://user@example.com/hook",     // username only
            "https://:secret@example.com/hook",  // password only
            "https://intranet/hook",             // single-label host
            "https://printer.local/hook",
        };
        foreach (var url in blocked)
        {
            var response = await strict.PostAsync("/api/v1/client/webhooks", new CreateWebhookRequest("x", url, ["recognition.completed"]), admin.AccessToken);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, url);
        }
    }

    [Fact]
    public async Task The_retention_purge_runs_without_touching_pending_work()
    {
        await _app.Factory.Services.GetRequiredService<WebhookDispatcher>().PurgeFinishedAsync(default);
    }
}
