using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Domain.Api;
using NexaVerify.Domain.Licensing;
using NexaVerify.Infrastructure.Background;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>Caps that must hold under parallel requests, and personal data in history that must not outlive its window.</summary>
[Collection(SqlServerCollection.Name)]
public class CapsAndRetentionTests : UsageTestBase
{
    public CapsAndRetentionTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    private async Task SetPlatformSettingAsync(Guid clientId, string key, int value) =>
        (await App.PutAsync($"/api/v1/admin/clients/{clientId}/settings", new { values = new Dictionary<string, int> { [key] = value } }, Platform.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

    [Fact]
    public async Task Parallel_enrolments_cannot_exceed_the_profile_limit_and_the_losers_are_not_charged()
    {
        var t = await NewTenantAsync("CAP1", credits: 100);
        await SetPlatformSettingAsync(t.ClientId, "limits.maxProfiles", 1);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => EnrollAsync(t.Token, "person-" + i, TestImages.Person(200 + i))));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        var losers = responses.Where(r => r.StatusCode != HttpStatusCode.OK).ToList();
        losers.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Conflict);
        foreach (var loser in losers)
        {
            (await loser.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString().ShouldBe("PROFILE_LIMIT_REACHED");
        }

        (await App.WithTenantDbAsync(t.ClientId, db => db.FaceProfiles.CountAsync())).ShouldBe(1);
        var (consumed, _, charged) = await LedgerTotalsAsync(t.ClientId);
        (consumed, charged).ShouldBe((1L, 1L)); // one enrolment, one credit: the rejected ones rolled back with their charge
    }

    [Fact]
    public async Task Parallel_key_creation_cannot_exceed_the_key_limit()
    {
        var t = await NewTenantAsync("CAP2");
        const int limit = 5; // the default

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            App.PostAsync("/api/v1/client/api-keys", new CreateApiKeyRequest("key-" + i, ["faces.read"], null, null, null), t.Token)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(limit);
        var refused = responses.Where(r => r.StatusCode != HttpStatusCode.Created).ToList();
        refused.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Conflict);
        (await refused[0].Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString().ShouldBe("APIKEY_LIMIT_REACHED");
        (await App.WithTenantDbAsync(t.ClientId, db => db.ApiKeys.CountAsync(k => k.Status == ApiKeyStatus.Active))).ShouldBe(limit);
    }

    [Fact]
    public async Task Regenerating_with_a_grace_period_cannot_slip_past_the_key_limit_in_parallel()
    {
        var t = await NewTenantAsync("CAP3");
        var created = new List<CreatedApiKeyDto>();
        for (var i = 0; i < 4; i++)
        {
            created.Add((await (await App.PostAsync("/api/v1/client/api-keys", new CreateApiKeyRequest("k" + i, ["faces.read"], null, null, null), t.Token)).Content.ReadFromJsonAsync<CreatedApiKeyDto>(AuthApp.Json))!);
        }

        // 4 keys active, limit 5: only ONE grace regeneration fits (it adds a live replacement while the old key lingers)
        var responses = await Task.WhenAll(created.Select(k =>
            App.PostAsync($"/api/v1/client/api-keys/{k.Key.Id}/regenerate", new RegenerateApiKeyRequest(60), t.Token)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        (await App.WithTenantDbAsync(t.ClientId, db => db.ApiKeys.CountAsync(k => k.Status == ApiKeyStatus.Active))).ShouldBe(5);
    }

    [Fact]
    public async Task Parallel_invitations_cannot_exceed_the_user_limit()
    {
        var t = await NewTenantAsync("CAP4");
        await SetPlatformSettingAsync(t.ClientId, "limits.maxUsers", 4); // the first admin already counts as one

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            App.PostAsync("/api/v1/client/users", new CreateClientUserRequest($"user{i}@cap4.test", "User " + i, SystemRoles.ClientUser, null), t.Token)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(3);
        responses.Where(r => r.StatusCode != HttpStatusCode.Created).ShouldAllBe(r => r.StatusCode == HttpStatusCode.Conflict);
        (await App.WithTenantDbAsync(t.ClientId, db => db.Users.CountAsync(u => u.Status == NexaVerify.Domain.Identity.UserStatus.Active))).ShouldBe(4);
    }

    [Fact]
    public async Task History_loses_the_image_fingerprint_and_ip_after_the_window_but_keeps_the_billing_facts()
    {
        var t = await NewTenantAsync("RET1", credits: 100);
        await RunRecognitionsAsync(t, seed: 20);
        await App.WithTenantDbAsync(t.ClientId, async db =>
        {
            await db.RecognitionRequests.ExecuteUpdateAsync(s => s.SetProperty(r => r.IpAddress, "203.0.113.9"));
            var oldest = await db.RecognitionRequests.OrderBy(r => r.CreatedAt).Take(3).Select(r => r.Id).ToListAsync();
            await db.RecognitionRequests.Where(r => oldest.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedAt, DateTime.UtcNow.AddDays(-120)));
            return true;
        });
        var before = await App.WithTenantDbAsync(t.ClientId, db => db.RecognitionRequests.AsNoTracking().ToListAsync());
        var sweeper = new FaceRetentionSweeper(App.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new FaceRetentionOptions { HistoryPersonalDataDays = 90 }), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FaceRetentionSweeper>.Instance);

        await sweeper.SweepOnceAsync(default);

        var after = await App.WithTenantDbAsync(t.ClientId, db => db.RecognitionRequests.AsNoTracking().ToListAsync());
        after.Count.ShouldBe(before.Count);
        var old = after.Where(r => r.CreatedAt < DateTime.UtcNow.AddDays(-90)).ToList();
        old.Count.ShouldBe(3);
        old.ShouldAllBe(r => r.IpAddress == null && r.InputImageSha256.All(b => b == 0));
        after.Except(old).ShouldAllBe(r => r.IpAddress == "203.0.113.9" && r.InputImageSha256.Any(b => b != 0));
        // outcome and cost are untouched
        foreach (var row in after)
        {
            var original = before.Single(b => b.Id == row.Id);
            (row.Outcome, row.CreditsCharged, row.Operation).ShouldBe((original.Outcome, original.CreditsCharged, original.Operation));
        }

        // the sweep is idempotent
        await sweeper.SweepOnceAsync(default);
        (await App.WithTenantDbAsync(t.ClientId, db => db.RecognitionRequests.CountAsync(r => r.IpAddress == null))).ShouldBe(3);
    }

    [Fact]
    public async Task The_sweeper_keeps_going_until_a_clients_backlog_is_drained()
    {
        var t = await NewTenantAsync("RET2", credits: 100);
        for (var i = 0; i < 5; i++)
        {
            (await EnrollAsync(t.Token, "old-" + i, TestImages.Person(300 + i))).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await App.WithTenantDbAsync(t.ClientId, async db =>
        {
            await db.FaceProfiles.ExecuteUpdateAsync(s => s.SetProperty(p => p.RetentionUntil, DateTime.UtcNow.AddMinutes(-5)));
            return true;
        });
        var sweeper = new FaceRetentionSweeper(App.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new FaceRetentionOptions { BatchSize = 2 }), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FaceRetentionSweeper>.Instance);

        (await sweeper.SweepOnceAsync(default)).ShouldBe(5); // three batches of 2, 2 and 1 in a single pass

        (await App.WithTenantDbAsync(t.ClientId, db => db.FaceProfiles.CountAsync())).ShouldBe(0);
    }
}

/// <summary>Webhook dispatcher hardening: suspended clients, cross-client deliveries and undecryptable secrets.</summary>
[Collection(SqlServerCollection.Name)]
public class WebhookHardeningTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private AuthApp _app = null!;
    private LoginResponse _platform = null!;
    private WebhookReceiver _receiver = null!;

    public WebhookHardeningTests(SqlServerFixture fixture)
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

    private async Task<Tenant> NewTenantWithHookAsync(string code)
    {
        var (client, admin) = await _app.OnboardClientAsync(_platform.AccessToken, code, $"a@{code.ToLowerInvariant()}.test");
        await _app.SeedLicenseAsync(client.Id, 100);
        (await _app.PostAsync("/api/v1/client/webhooks", new CreateWebhookRequest("ci", _receiver.Url, ["recognition.completed"]), admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _app.PostFormAsync("/api/v1/faces/enroll", TestImages.Person(1), new Dictionary<string, string> { ["externalRef"] = "e-1", ["consentReference"] = "c" }, admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        return new Tenant(client.Id, admin.AccessToken);
    }

    private Task<int> DispatchAsync() => _app.Factory.Services.GetRequiredService<WebhookDispatcher>().DispatchDueAsync(default);

    private Task MakeDueAsync() => _app.WithDbAsync(async db =>
    {
        await db.WebhookDeliveries.Where(d => d.Status == DeliveryStatus.Pending).ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
        return true;
    });

    private Task<List<WebhookDelivery>> DeliveriesAsync() => _app.WithDbAsync(db => db.WebhookDeliveries.AsNoTracking().OrderBy(d => d.Id).ToListAsync());

    [Fact]
    public async Task A_suspended_client_receives_nothing_and_its_events_are_delivered_after_reactivation()
    {
        var t = await NewTenantWithHookAsync("WH1");
        (await _app.PostAsync($"/api/v1/admin/clients/{t.ClientId}/suspend", new ClientStatusRequest("review"), _platform.AccessToken)).IsSuccessStatusCode.ShouldBeTrue();
        await MakeDueAsync();

        (await DispatchAsync()).ShouldBe(1);

        _receiver.Requests.ShouldBeEmpty();
        var held = (await DeliveriesAsync()).ShouldHaveSingleItem();
        (held.Status, held.Attempts).ShouldBe((DeliveryStatus.Pending, 0));
        held.NextAttemptAt.ShouldBeGreaterThan(DateTime.UtcNow.AddMinutes(1)); // deferred, not hammered every poll

        (await _app.PostAsync($"/api/v1/admin/clients/{t.ClientId}/activate", null, _platform.AccessToken)).IsSuccessStatusCode.ShouldBeTrue();
        await MakeDueAsync();
        (await DispatchAsync()).ShouldBe(1);

        _receiver.Requests.Count.ShouldBe(1);
        (await DeliveriesAsync()).ShouldHaveSingleItem().Status.ShouldBe(DeliveryStatus.Delivered);
    }

    [Fact]
    public async Task A_delivery_whose_client_differs_from_its_endpoints_client_is_abandoned_unsent()
    {
        var a = await NewTenantWithHookAsync("WH2");
        var (other, _) = await _app.OnboardClientAsync(_platform.AccessToken, "WH2B", "a@wh2b.test");
        await _app.WithDbAsync(async db =>
        {
            await db.WebhookDeliveries.ExecuteUpdateAsync(s => s.SetProperty(d => d.ClientId, other.Id));
            return true;
        });
        await MakeDueAsync();

        (await DispatchAsync()).ShouldBe(1);

        _receiver.Requests.ShouldBeEmpty();
        var delivery = (await DeliveriesAsync()).ShouldHaveSingleItem();
        delivery.Status.ShouldBe(DeliveryStatus.Abandoned);
        delivery.LastError.ShouldNotBeNull().ShouldContain("does not belong");
        a.ClientId.ShouldNotBe(other.Id);
    }

    [Fact]
    public async Task A_delivery_whose_secret_cannot_be_decrypted_is_abandoned_after_a_few_attempts()
    {
        await NewTenantWithHookAsync("WH3");
        await _app.WithDbAsync(async db =>
        {
            await db.WebhookEndpoints.ExecuteUpdateAsync(s => s.SetProperty(e => e.SecretEnc, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48 }));
            return true;
        });

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await MakeDueAsync();
            (await DispatchAsync()).ShouldBe(1);
            var state = (await DeliveriesAsync()).Single();
            state.Attempts.ShouldBe(attempt);
            state.Status.ShouldBe(attempt < 3 ? DeliveryStatus.Pending : DeliveryStatus.Abandoned);
        }

        await MakeDueAsync();
        (await DispatchAsync()).ShouldBe(0); // nothing left to retry
        _receiver.Requests.ShouldBeEmpty();
        (await DeliveriesAsync()).Single().LastError.ShouldNotBeNull().ShouldNotContain("Cryptographic");
    }
}
