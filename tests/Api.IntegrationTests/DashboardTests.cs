using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Dashboards;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Domain.Licensing;
using NexaVerify.Infrastructure.Background;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>Client and admin dashboards end to end: numbers, tenant isolation, permissions, validation and ledger reconciliation.</summary>
[Collection(SqlServerCollection.Name)]
public class DashboardTests : UsageTestBase
{
    public DashboardTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    // ---- client dashboard ----

    [Fact]
    public async Task The_client_dashboard_shows_the_clients_own_recognitions_credits_and_balance()
    {
        var a = await NewTenantAsync("D1", credits: 100);
        var charged = await RunRecognitionsAsync(a, seed: 10);

        var dash = await GetAsync<ClientDashboardDto>("/api/v1/client/dashboard", a.Token);

        dash.Days.ShouldBe(30);
        dash.RecognitionsPerDay.Count.ShouldBe(30);
        dash.CreditsPerDay.Count.ShouldBe(30);
        dash.ApiPerDay.Count.ShouldBe(30);
        dash.Recognitions.Total.ShouldBe(5);
        (dash.Recognitions.Successful, dash.Recognitions.NoMatch, dash.Recognitions.Failed).ShouldBe((4L, 1L, 0L));
        dash.Recognitions.SuccessRate.ShouldBe(0.8m);
        dash.Recognitions.NoMatchRate.ShouldBe(0.2m);
        dash.Recognitions.ErrorRate.ShouldBe(0m);
        dash.RecognitionBreakdown.Select(b => (b.Operation, b.Outcome, b.Count)).OrderBy(b => b.Operation).ThenBy(b => b.Outcome).ToList()
            .ShouldBe([("Enroll", "Enrolled", 2L), ("Identify", "Matched", 1L), ("Verify", "Matched", 1L), ("Verify", "NoMatch", 1L)]);
        dash.Credits.ShouldBe(new CreditSummaryDto(charged, 0, charged));
        dash.Licenses.RemainingCredits.ShouldBe(100 - charged);
        dash.Licenses.TotalCredits.ShouldBe(100);
    }

    [Fact]
    public async Task One_client_never_sees_another_clients_numbers()
    {
        var a = await NewTenantAsync("D2A", credits: 100);
        var b = await NewTenantAsync("D2B", credits: 100);
        await RunRecognitionsAsync(a, seed: 20);
        (await EnrollAsync(b.Token, "only-b", TestImages.Person(90))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var dashA = await GetAsync<ClientDashboardDto>("/api/v1/client/dashboard", a.Token);
        var dashB = await GetAsync<ClientDashboardDto>("/api/v1/client/dashboard", b.Token);

        dashA.Recognitions.Total.ShouldBe(5);
        dashA.Credits.Consumed.ShouldBe(6);
        dashB.Recognitions.Total.ShouldBe(1);
        dashB.Credits.Consumed.ShouldBe(1);
        dashB.Licenses.Licenses.ShouldAllBe(l => l.ClientId == b.ClientId);
        dashB.Licenses.TotalCredits.ShouldBe(100);
    }

    [Fact]
    public async Task The_window_is_validated_and_capped()
    {
        var a = await NewTenantAsync("D3");

        foreach (var bad in new[] { "0", "-5", "91", "1000", "abc" })
        {
            var response = await App.GetAsync($"/api/v1/client/dashboard?days={bad}", a.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"days={bad}");
        }

        (await GetAsync<ClientDashboardDto>("/api/v1/client/dashboard?days=90", a.Token)).RecognitionsPerDay.Count.ShouldBe(90);
        (await GetAsync<ClientDashboardDto>("/api/v1/client/dashboard?days=1", a.Token)).RecognitionsPerDay.Count.ShouldBe(1);

        var invalid = await App.GetAsync("/api/v1/admin/dashboard?days=91", Platform.AccessToken);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadFromJsonAsync<JsonElement>(AuthApp.Json)).GetProperty("code").GetString().ShouldBe(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Dashboards_require_authentication_and_the_right_permission()
    {
        var a = await NewTenantAsync("D4");

        (await App.GetAsync("/api/v1/client/dashboard")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await App.GetAsync("/api/v1/admin/dashboard")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await App.GetAsync("/api/v1/client/dashboard", Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // staff have no client dashboard
        (await App.GetAsync("/api/v1/admin/dashboard", a.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // clients never see the platform's
    }

    [Fact]
    public async Task An_api_key_cannot_read_the_dashboard()
    {
        var a = await NewTenantAsync("D5");
        var created = await App.PostAsync("/api/v1/client/api-keys", new CreateApiKeyRequest("ci", ["faces.read"], null, null, null), a.Token);
        var raw = (await created.Content.ReadFromJsonAsync<CreatedApiKeyDto>(AuthApp.Json))!.RawKey;

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/client/dashboard");
        request.Headers.Add("X-Api-Key", raw);

        (await App.Client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Api_traffic_is_counted_per_client_with_errors_latency_and_top_keys()
    {
        var a = await NewTenantAsync("D6", credits: 100);
        var b = await NewTenantAsync("D6B", credits: 100);
        var keyA = (await (await App.PostAsync("/api/v1/client/api-keys",
            new CreateApiKeyRequest("Kiosk", ["faces.read", "faces.enroll", "faces.verify"], null, null, null), a.Token)).Content.ReadFromJsonAsync<CreatedApiKeyDto>(AuthApp.Json))!;
        var keyB = (await (await App.PostAsync("/api/v1/client/api-keys",
            new CreateApiKeyRequest("Other", ["faces.read"], null, null, null), b.Token)).Content.ReadFromJsonAsync<CreatedApiKeyDto>(AuthApp.Json))!;

        async Task<HttpStatusCode> CallAsync(string raw, string path)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-Api-Key", raw);
            return (await App.Client.SendAsync(request)).StatusCode;
        }

        for (var i = 0; i < 4; i++)
        {
            (await CallAsync(keyA.RawKey, "/api/v1/faces/balance")).ShouldBe(HttpStatusCode.OK);
        }

        (await CallAsync(keyA.RawKey, "/api/v1/faces/profiles/" + Guid.NewGuid())).ShouldBe(HttpStatusCode.NotFound);
        (await CallAsync(keyB.RawKey, "/api/v1/faces/balance")).ShouldBe(HttpStatusCode.OK);
        await App.Factory.Services.GetRequiredService<ApiRequestLogWriter>().FlushAsync(default);

        var dash = await GetAsync<ClientDashboardDto>("/api/v1/client/dashboard", a.Token);

        dash.Api.Requests.ShouldBe(5);
        dash.Api.Errors.ShouldBe(1);
        dash.Api.ServerErrors.ShouldBe(0);
        dash.Api.ErrorRate.ShouldBe(0.2m);
        dash.Api.P95LatencyMs.ShouldNotBeNull();
        (dash.Api.P95LatencyMs!.Value % 25).ShouldBe(0); // a bucket edge: the p95 is exact to the configured resolution
        dash.ApiPerDay.Sum(d => d.Requests).ShouldBe(5);
        var top = dash.TopApiKeys.ShouldHaveSingleItem();
        (top.ApiKeyId, top.Name, top.Requests, top.Errors).ShouldBe((keyA.Key.Id, "Kiosk", 5L, 1L));
        top.KeyPrefix.ShouldBe(keyA.Key.Prefix);

        var dashB = await GetAsync<ClientDashboardDto>("/api/v1/client/dashboard", b.Token);
        dashB.Api.Requests.ShouldBe(1);
        dashB.TopApiKeys.Single().Name.ShouldBe("Other");
    }

    // ---- admin dashboard ----

    [Fact]
    public async Task The_admin_dashboard_summarises_the_platform_from_ledger_and_logs_without_any_face_data()
    {
        var a = await NewTenantAsync("D7A", credits: 100);
        var b = await NewTenantAsync("D7B", credits: 100);
        var lowAndEnding = await NewTenantAsync("D7C", credits: 100, licenseEnds: DateTime.UtcNow.AddDays(10));
        await RunRecognitionsAsync(a, seed: 30); // 6 credits, 5 billed operations
        (await EnrollAsync(b.Token, "person-ref-b", TestImages.Person(95))).StatusCode.ShouldBe(HttpStatusCode.OK); // 1 credit
        await App.WithDbAsync(async db =>
        {
            await db.Licenses.Where(l => l.Id == lowAndEnding.LicenseId).ExecuteUpdateAsync(s => s.SetProperty(l => l.ConsumedCredits, 95));
            return true;
        });

        var raw = await App.GetAsync("/api/v1/admin/dashboard", Platform.AccessToken);
        raw.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await raw.Content.ReadAsStringAsync();
        var dash = JsonSerializer.Deserialize<AdminDashboardDto>(json, AuthApp.Json)!;

        dash.ClientsByStatus.Single(c => c.Label == "Active").Count.ShouldBe(3);
        dash.LicensesByStatus.Single(c => c.Label == "Active").Count.ShouldBe(3);
        dash.Credits.Consumed.ShouldBe(7);
        dash.BilledOperationsPerDay.Sum(o => o.Count).ShouldBe(6);
        dash.TopClients.Select(c => (c.ClientCode, c.CreditsConsumed)).ShouldBe([("D7A", 6L), ("D7B", 1L)]);
        dash.TopClients[0].BilledOperations.ShouldBe(5);
        dash.ExpiringLicenses.Count.ShouldBe(1);
        dash.ExpiringLicenses.Items.Single().ClientId.ShouldBe(lowAndEnding.ClientId);
        dash.LowBalanceLicenses.Threshold.ShouldBe(10);
        var low = dash.LowBalanceLicenses.Items.ShouldHaveSingleItem();
        (low.ClientId, low.RemainingCredits, low.PercentRemaining).ShouldBe((lowAndEnding.ClientId, 5L, 5));
        dash.Webhooks.ShouldBe(new WebhookHealthDto(0, 0, 0, 0, 0));

        // aggregates only: nothing that could identify a person or a template
        foreach (var forbidden in new[] { "person-ref-b", "emp-1", "externalRef", "profile", "template", "embedding", "displayName" })
        {
            json.ShouldNotContain(forbidden, Case.Insensitive);
        }
    }

    [Fact]
    public async Task Platform_scope_cannot_read_the_strict_face_tables_so_admin_volume_comes_from_the_ledger()
    {
        var a = await NewTenantAsync("D8");
        await RunRecognitionsAsync(a, seed: 40);

        var platformSeesRequests = await App.WithDbAsync(db => db.RecognitionRequests.CountAsync());
        var tenantSeesRequests = await App.WithTenantDbAsync(a.ClientId, db => db.RecognitionRequests.CountAsync());

        platformSeesRequests.ShouldBe(0);
        tenantSeesRequests.ShouldBe(5);
        (await GetAsync<AdminDashboardDto>("/api/v1/admin/dashboard", Platform.AccessToken)).BilledOperationsPerDay.Sum(o => o.Count).ShouldBe(5);
    }

    // ---- reconciliation ----

    [Fact]
    public async Task Dashboard_credit_totals_equal_the_ledger_and_the_recognition_charges_even_after_adjustments_and_refunds()
    {
        var t = await NewTenantAsync("D9", credits: 100);
        await RunRecognitionsAsync(t, seed: 50);

        // an adjustment up and down and a refund of one recognition's charge (admin operations that must not disturb "consumed")
        (await App.PostAsync($"/api/v1/admin/licenses/{t.LicenseId}/adjust", new AdjustLicenseRequest(40, "goodwill"), Platform.AccessToken)).EnsureSuccessStatusCode();
        (await App.PostAsync($"/api/v1/admin/licenses/{t.LicenseId}/adjust", new AdjustLicenseRequest(-5, "correction"), Platform.AccessToken)).EnsureSuccessStatusCode();
        var identifyCharge = await App.WithTenantDbAsync(t.ClientId, db => db.LicenseTransactions.AsNoTracking()
            .Where(x => x.Type == LedgerEntryType.Consume && x.Operation == MeteredOperation.Identify).Select(x => x.Id).SingleAsync());
        (await App.PostAsync($"/api/v1/admin/transactions/{identifyCharge}/refund", new RefundRequest("test refund"), Platform.AccessToken)).EnsureSuccessStatusCode();
        await RunRecognitionsAsync(t, seed: 60); // a second round after the adjustments

        var (ledgerConsumed, ledgerRefunded, charged) = await LedgerTotalsAsync(t.ClientId);
        ledgerConsumed.ShouldBe(12);
        ledgerRefunded.ShouldBe(2);
        charged.ShouldBe(ledgerConsumed); // every credit consumed in the ledger belongs to exactly one recognition

        var client = await GetAsync<ClientDashboardDto>("/api/v1/client/dashboard", t.Token);
        client.Credits.ShouldBe(new CreditSummaryDto(ledgerConsumed, ledgerRefunded, ledgerConsumed - ledgerRefunded));
        client.CreditsPerDay.Sum(d => d.Consumed).ShouldBe(ledgerConsumed);
        client.CreditsPerDay.Sum(d => d.Refunded).ShouldBe(ledgerRefunded);
        client.Credits.Consumed.ShouldBe(charged);

        var admin = await GetAsync<AdminDashboardDto>("/api/v1/admin/dashboard", Platform.AccessToken);
        admin.Credits.ShouldBe(client.Credits); // the only client with activity: platform == client == ledger
        admin.CreditsPerDay.Select(d => (d.Date, d.Consumed, d.Refunded)).ShouldBe(client.CreditsPerDay.Select(d => (d.Date, d.Consumed, d.Refunded)));
        admin.TopClients.Single().CreditsConsumed.ShouldBe(ledgerConsumed);

        // and the balance the client sees is the ledger's too: 100 + 40 - 5 - 12 + 2
        client.Licenses.RemainingCredits.ShouldBe(125);
        (await GetAsync<LedgerVerificationDto>($"/api/v1/admin/licenses/{t.LicenseId}/verify-ledger", Platform.AccessToken)).Valid.ShouldBeTrue();
    }
}
