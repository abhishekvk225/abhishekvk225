using System.Net;
using System.Net.Http.Json;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Licensing;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class LicensingTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private AuthApp _app = null!;
    private LoginResponse _platform = null!;

    public LicensingTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _app = await AuthApp.CreateAsync(_fixture);
        _platform = await _app.SuperAdminAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task<LicenseDto> CreateLicenseAsync(Guid clientId, int credits = 100, int days = 30, string name = "Annual")
    {
        var response = await _app.PostAsync($"/api/v1/admin/clients/{clientId}/licenses",
            new CreateLicenseRequest(null, name, credits, null, DateTime.UtcNow.AddDays(days), null), _platform.AccessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<LicenseDto>(AuthApp.Json))!;
    }

    private async Task<LicenseDto> GetAsync(Guid id) =>
        (await (await _app.GetAsync($"/api/v1/admin/licenses/{id}", _platform.AccessToken)).Content.ReadFromJsonAsync<LicenseDto>(AuthApp.Json))!;

    [Fact]
    public async Task Seeded_plans_and_default_costs_exist()
    {
        var plans = (await (await _app.GetAsync("/api/v1/admin/plans", _platform.AccessToken)).Content.ReadFromJsonAsync<List<PlanDto>>(AuthApp.Json))!;
        plans.Select(p => p.Code).ShouldBe(["BUSINESS", "ENTERPRISE", "STARTER", "TRIAL"], ignoreOrder: true);

        var rules = (await (await _app.GetAsync("/api/v1/admin/cost-rules", _platform.AccessToken)).Content.ReadFromJsonAsync<List<CostRuleDto>>(AuthApp.Json))!;
        rules.Single(r => r.Operation == "Detect").Credits.ShouldBe(0);
        rules.Single(r => r.Operation == "Identify").Credits.ShouldBe(2);
    }

    [Fact]
    public async Task Creating_a_license_issues_a_key_defaults_from_the_plan_and_writes_a_grant()
    {
        var (client, _) = await _app.OnboardClientAsync(_platform.AccessToken, "LIC1", "a@lic1.test");
        var plan = (await (await _app.GetAsync("/api/v1/admin/plans", _platform.AccessToken)).Content.ReadFromJsonAsync<List<PlanDto>>(AuthApp.Json))!.Single(p => p.Code == "TRIAL");

        var response = await _app.PostAsync($"/api/v1/admin/clients/{client.Id}/licenses", new CreateLicenseRequest(plan.Id, "Trial licence", null, null, null, null), _platform.AccessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var license = (await response.Content.ReadFromJsonAsync<LicenseDto>(AuthApp.Json))!;

        license.LicenseKey.ShouldStartWith("NXV-");
        license.TotalCredits.ShouldBe(plan.DefaultCredits);
        license.RemainingCredits.ShouldBe(plan.DefaultCredits);
        license.Status.ShouldBe("Active");
        license.DaysRemaining.ShouldBeInRange(plan.DefaultDurationDays - 1, plan.DefaultDurationDays);

        var ledger = (await (await _app.GetAsync($"/api/v1/admin/licenses/{license.Id}/transactions", _platform.AccessToken)).Content.ReadFromJsonAsync<PagedResult<LicenseTransactionDto>>(AuthApp.Json))!;
        var grant = ledger.Items.Single();
        grant.Type.ShouldBe("Grant");
        grant.Credits.ShouldBe(plan.DefaultCredits);
        grant.BalanceAfter.ShouldBe(plan.DefaultCredits);

        var verification = (await (await _app.GetAsync($"/api/v1/admin/licenses/{license.Id}/verify-ledger", _platform.AccessToken)).Content.ReadFromJsonAsync<LedgerVerificationDto>(AuthApp.Json))!;
        verification.Valid.ShouldBeTrue();
    }

    [Fact]
    public async Task Creation_validates_input_and_requires_credits_and_an_end_date_without_a_plan()
    {
        var (client, _) = await _app.OnboardClientAsync(_platform.AccessToken, "LIC2", "a@lic2.test");
        var url = $"/api/v1/admin/clients/{client.Id}/licenses";

        (await _app.PostAsync(url, new CreateLicenseRequest(null, "", 10, null, DateTime.UtcNow.AddDays(1), null), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.PostAsync(url, new CreateLicenseRequest(null, "x", null, null, DateTime.UtcNow.AddDays(1), null), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.PostAsync(url, new CreateLicenseRequest(null, "x", 10, null, null, null), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.PostAsync(url, new CreateLicenseRequest(null, "x", 10, DateTime.UtcNow.AddDays(2), DateTime.UtcNow.AddDays(1), null), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.PostAsync(url, new CreateLicenseRequest(Guid.NewGuid(), "x", 10, null, DateTime.UtcNow.AddDays(1), null), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.PostAsync($"/api/v1/admin/clients/{Guid.NewGuid()}/licenses", new CreateLicenseRequest(null, "x", 10, null, DateTime.UtcNow.AddDays(1), null), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Status_lifecycle_follows_the_state_machine()
    {
        var (client, _) = await _app.OnboardClientAsync(_platform.AccessToken, "LIC3", "a@lic3.test");
        var license = await CreateLicenseAsync(client.Id);
        var baseUrl = $"/api/v1/admin/licenses/{license.Id}";

        (await _app.PostAsync($"{baseUrl}/suspend", new LicenseReasonRequest(null), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.PostAsync($"{baseUrl}/suspend", new LicenseReasonRequest("Payment overdue"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(license.Id)).Status.ShouldBe("Suspended");

        (await _app.PostAsync($"{baseUrl}/suspend", new LicenseReasonRequest("again"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _app.PostAsync($"{baseUrl}/activate", null, _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _app.PostAsync($"{baseUrl}/deactivate", null, _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(license.Id)).Status.ShouldBe("Inactive");
        (await _app.PostAsync($"{baseUrl}/activate", null, _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await _app.PostAsync($"{baseUrl}/revoke", new LicenseReasonRequest("Contract ended"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var revoked = await GetAsync(license.Id);
        revoked.Status.ShouldBe("Revoked");
        revoked.RemainingCredits.ShouldBe(0);
        (await _app.PostAsync($"{baseUrl}/activate", null, _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _app.PostAsync($"{baseUrl}/renew", new RenewLicenseRequest(DateTime.UtcNow.AddDays(90), 10, null), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var verification = (await (await _app.GetAsync($"{baseUrl}/verify-ledger", _platform.AccessToken)).Content.ReadFromJsonAsync<LedgerVerificationDto>(AuthApp.Json))!;
        verification.Valid.ShouldBeTrue();
        verification.Entries.ShouldBe(2); // grant + revocation write-off
    }

    [Fact]
    public async Task Renewal_extends_the_period_and_credits_and_adjustments_never_go_negative()
    {
        var (client, _) = await _app.OnboardClientAsync(_platform.AccessToken, "LIC4", "a@lic4.test");
        var license = await CreateLicenseAsync(client.Id, credits: 100);
        var baseUrl = $"/api/v1/admin/licenses/{license.Id}";

        var newEnd = license.ExpiresAt.AddDays(60);
        var renewed = (await (await _app.PostAsync($"{baseUrl}/renew", new RenewLicenseRequest(newEnd, 50, "Top-up"), _platform.AccessToken)).Content.ReadFromJsonAsync<LicenseDto>(AuthApp.Json))!;
        renewed.TotalCredits.ShouldBe(150);
        renewed.ExpiresAt.ShouldBe(newEnd, TimeSpan.FromSeconds(1));

        (await _app.PostAsync($"{baseUrl}/renew", new RenewLicenseRequest(license.ExpiresAt, 0, null), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // not later

        (await _app.PostAsync($"{baseUrl}/adjust", new AdjustLicenseRequest(-20, "Correction"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(license.Id)).RemainingCredits.ShouldBe(130);
        (await _app.PostAsync($"{baseUrl}/adjust", new AdjustLicenseRequest(-1000, "Oops"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.PostAsync($"{baseUrl}/adjust", new AdjustLicenseRequest(0, "none"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.PostAsync($"{baseUrl}/adjust", new AdjustLicenseRequest(5, ""), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var verification = (await (await _app.GetAsync($"{baseUrl}/verify-ledger", _platform.AccessToken)).Content.ReadFromJsonAsync<LedgerVerificationDto>(AuthApp.Json))!;
        verification.Valid.ShouldBeTrue();
    }

    [Fact]
    public async Task Concurrent_edits_are_detected_with_the_row_version()
    {
        var (client, _) = await _app.OnboardClientAsync(_platform.AccessToken, "LIC5", "a@lic5.test");
        var license = await CreateLicenseAsync(client.Id);
        var url = $"/api/v1/admin/licenses/{license.Id}";

        (await _app.PutAsync(url, new UpdateLicenseRequest("Renamed", "n", license.RowVersion), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _app.PutAsync(url, new UpdateLicenseRequest("Stale", "n", license.RowVersion), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Clients_see_only_their_own_licenses_and_cannot_use_admin_endpoints()
    {
        var (a, adminA) = await _app.OnboardClientAsync(_platform.AccessToken, "LICA", "a@lica.test");
        var (b, adminB) = await _app.OnboardClientAsync(_platform.AccessToken, "LICB", "a@licb.test");
        var licenseA = await CreateLicenseAsync(a.Id, 100, days: 200, name: "A-licence");
        await CreateLicenseAsync(b.Id, 50, name: "B-licence");

        var summaryA = (await (await _app.GetAsync("/api/v1/client/licenses/summary", adminA.AccessToken)).Content.ReadFromJsonAsync<LicenseSummaryDto>(AuthApp.Json))!;
        summaryA.RemainingCredits.ShouldBe(100);
        summaryA.Licenses.Single().Name.ShouldBe("A-licence");
        summaryA.Health.ShouldBe("Healthy");

        var summaryB = (await (await _app.GetAsync("/api/v1/client/licenses/summary", adminB.AccessToken)).Content.ReadFromJsonAsync<LicenseSummaryDto>(AuthApp.Json))!;
        summaryB.RemainingCredits.ShouldBe(50);

        // B cannot read A's license or ledger by id (404, not 403: its existence is not revealed)
        (await _app.GetAsync($"/api/v1/client/licenses/{licenseA.Id}", adminB.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _app.GetAsync($"/api/v1/client/licenses/{licenseA.Id}/transactions", adminB.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _app.GetAsync($"/api/v1/client/licenses/{licenseA.Id}", adminA.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // client principals have no admin permissions
        (await _app.GetAsync("/api/v1/admin/licenses", adminA.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.PostAsync($"/api/v1/admin/clients/{a.Id}/licenses", new CreateLicenseRequest(null, "self-grant", 1_000_000, null, DateTime.UtcNow.AddDays(30), null), adminA.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.PostAsync($"/api/v1/admin/licenses/{licenseA.Id}/adjust", new AdjustLicenseRequest(1000, "free credits"), adminA.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // unauthenticated
        (await _app.GetAsync("/api/v1/client/licenses/summary")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Platform_listing_filters_by_client_status_and_expiry_window()
    {
        var (client, _) = await _app.OnboardClientAsync(_platform.AccessToken, "LIC6", "a@lic6.test");
        await CreateLicenseAsync(client.Id, 10, days: 5, name: "Soon");
        await CreateLicenseAsync(client.Id, 10, days: 300, name: "Later");
        var suspended = await CreateLicenseAsync(client.Id, 10, days: 100, name: "Paused");
        await _app.PostAsync($"/api/v1/admin/licenses/{suspended.Id}/suspend", new LicenseReasonRequest("test"), _platform.AccessToken);

        async Task<PagedResult<LicenseListItemDto>> ListAsync(string query) =>
            (await (await _app.GetAsync($"/api/v1/admin/licenses?clientId={client.Id}&{query}", _platform.AccessToken)).Content.ReadFromJsonAsync<PagedResult<LicenseListItemDto>>(AuthApp.Json))!;

        (await ListAsync("")).TotalCount.ShouldBe(3);
        (await ListAsync("expiringInDays=30")).Items.Select(i => i.Name).ShouldBe(["Soon"]);
        (await ListAsync("status=Suspended")).Items.Select(i => i.Name).ShouldBe(["Paused"]);
        (await ListAsync("search=Later")).Items.Select(i => i.Name).ShouldBe(["Later"]);
        (await _app.GetAsync("/api/v1/admin/licenses?status=Bogus", _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cost_rules_are_versioned_and_a_client_override_wins()
    {
        var (client, _) = await _app.OnboardClientAsync(_platform.AccessToken, "LIC7", "a@lic7.test");

        (await _app.PutAsync("/api/v1/admin/cost-rules/default", new SetCostRuleRequest("Verify", 3, "OnCompleted", null), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var rules = (await (await _app.GetAsync("/api/v1/admin/cost-rules", _platform.AccessToken)).Content.ReadFromJsonAsync<List<CostRuleDto>>(AuthApp.Json))!;
        rules.Count(r => r.Operation == "Verify" && r.EffectiveTo is null).ShouldBe(1);
        rules.Count(r => r.Operation == "Verify").ShouldBe(2); // the old rule was closed, not edited

        (await _app.PutAsync($"/api/v1/admin/cost-rules/clients/{client.Id}", new SetCostRuleRequest("Verify", 0, "OnSuccess", null), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _app.PutAsync("/api/v1/admin/cost-rules/default", new SetCostRuleRequest("Nope", 1, "OnSuccess", null), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.PutAsync("/api/v1/admin/cost-rules/default", new SetCostRuleRequest("Verify", 5000, "OnSuccess", null), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
