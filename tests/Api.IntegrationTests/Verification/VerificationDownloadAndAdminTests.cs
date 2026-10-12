using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Contracts.Verification;
using NexaVerify.TestSupport;
using NexaVerify.TestSupport.Verification;

namespace NexaVerify.Api.IntegrationTests.Verification;

/// <summary>Short-lived report downloads and the platform administration of the module (switches, providers, stats, investigation, audit).</summary>
[Collection(SqlServerCollection.Name)]
public class VerificationDownloadAndAdminTests : VerificationTestBase
{
    public VerificationDownloadAndAdminTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    private async Task<Guid> FinishedBankStatementAsync(Tenant t)
    {
        var accepted = await AcceptedAsync(await SubmitBankAsync(t));
        await RunWorkerAsync();
        return accepted.RequestId;
    }

    private async Task<DownloadLinkDto> LinkAsync(Tenant t, Guid id, string kind, string? format = null)
    {
        var response = await App.PostAsync($"/api/v1/verifications/{id}/downloads", new VerificationDownloadRequest(kind, format), t.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ReadAsync<DownloadLinkDto>(response);
    }

    // ---- downloads ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_report_link_works_once_and_the_file_is_served_safely()
    {
        var t = await NewTenantAsync("D1");
        var id = await FinishedBankStatementAsync(t);

        var link = await LinkAsync(t, id, "Report", "json");
        link.DownloadUrl.ShouldStartWith("/api/v1/verifications/downloads/");
        link.ExpiresAt.ShouldBeGreaterThan(DateTime.UtcNow);
        link.ExpiresAt.ShouldBeLessThan(DateTime.UtcNow.AddMinutes(5));

        var file = await App.GetAsync(link.DownloadUrl, t.Token);
        file.StatusCode.ShouldBe(HttpStatusCode.OK);
        file.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        file.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        file.Headers.CacheControl!.NoStore.ShouldBeTrue();
        file.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        var body = await file.Content.ReadAsStringAsync();
        body.ShouldContain("does not certify");
        body.ShouldNotContain("9900112233");

        var second = await App.GetAsync(link.DownloadUrl, t.Token);
        second.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await CodeOf(second)).ShouldBe(ErrorCodes.DownloadLinkExpired);

        var audit = await InTenantAsync(t, db => db.AuditLogs.AsNoTracking().Where(a => a.Action == "verification.report_downloaded").CountAsync());
        audit.ShouldBe(1);
        (await App.GetAsync("/api/v1/verifications/downloads/not-a-real-token", t.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await App.GetAsync(link.DownloadUrl)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_html_report_encodes_everything_and_the_transactions_csv_is_a_download_too()
    {
        var t = await NewTenantAsync("D2");
        var id = await FinishedBankStatementAsync(t);

        var html = await App.GetAsync((await LinkAsync(t, id, "Report", "html")).DownloadUrl, t.Token);
        html.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        html.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        html.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("default-src 'none'");
        var text = await html.Content.ReadAsStringAsync();
        text.ShouldContain("Verification report");
        text.ShouldContain("does not certify");
        text.ShouldNotContain("<script", Case.Insensitive);
        text.ShouldNotContain("authentic", Case.Insensitive);

        var csv = await App.GetAsync((await LinkAsync(t, id, "TransactionsCsv")).DownloadUrl, t.Token);
        csv.StatusCode.ShouldBe(HttpStatusCode.OK);
        csv.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var bytes = await csv.Content.ReadAsByteArrayAsync();
        bytes.Take(3).ShouldBe([0xEF, 0xBB, 0xBF]);
        Encoding.UTF8.GetString(bytes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(1 + 4);

        // A transactions export only exists for bank statements; a report only once processing finished.
        var pan = await AcceptedAsync(await SubmitPanAsync(t, PanPdf()));
        (await App.PostAsync($"/api/v1/verifications/{pan.RequestId}/downloads", new VerificationDownloadRequest("Report", "json"), t.Token)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await RunWorkerAsync();
        (await App.PostAsync($"/api/v1/verifications/{pan.RequestId}/downloads", new VerificationDownloadRequest("TransactionsCsv", null), t.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await App.PostAsync($"/api/v1/verifications/{pan.RequestId}/downloads", new VerificationDownloadRequest("Everything", null), t.Token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_link_belongs_to_the_principal_that_asked_for_it_and_expires()
    {
        var t = await NewTenantAsync("D3");
        var id = await FinishedBankStatementAsync(t);
        await App.CreateClientUserAsync(t.ClientId, "second@d3.test", "ClientAdmin");
        var second = await App.LoginAsync("second@d3.test", AuthApp.StrongPassword);

        var link = await LinkAsync(t, id, "Report", "json");
        (await App.GetAsync(link.DownloadUrl, second.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound); // not the principal it was issued to
        (await App.GetAsync(link.DownloadUrl, t.Token)).StatusCode.ShouldBe(HttpStatusCode.OK); // and that attempt did not burn it

        var expiring = await LinkAsync(t, id, "Report", "json");
        await App.WithTenantDbAsync(t.ClientId, db => db.VerificationDownloadGrants.ExecuteUpdateAsync(s => s.SetProperty(g => g.ExpiresAt, DateTime.UtcNow.AddMinutes(-1))));
        (await App.GetAsync(expiring.DownloadUrl, t.Token)).StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task The_original_document_cannot_be_downloaded_unless_the_operator_enabled_it()
    {
        var t = await NewTenantAsync("D4");
        var id = await FinishedBankStatementAsync(t);
        var refused = await App.PostAsync($"/api/v1/verifications/{id}/downloads", new VerificationDownloadRequest("OriginalDocument", null), t.Token);
        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- administration -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_admin_module_list_shows_every_module_and_provider_status_without_secrets()
    {
        var response = await App.GetAsync("/api/v1/admin/verifications/modules", Platform.AccessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync();
        text.ShouldNotContain("apikey", Case.Insensitive);
        text.ShouldNotContain("secret", Case.Insensitive);
        text.ShouldNotContain("password", Case.Insensitive);

        var modules = await ReadAsync<List<AdminVerificationModuleDto>>(response);
        modules.Count.ShouldBe(9);
        modules.Where(m => m.Status == "Upcoming").Count().ShouldBe(6);
        modules.Where(m => m.Status == "Upcoming").ShouldAllBe(m => m.Costs.Count == 0 && m.Providers.Count == 0 && m.IsEnabled == null);

        var pan = modules.Single(m => m.Key == "pan");
        pan.Costs.Select(c => (c.Operation, c.Credits)).ShouldBe([("PanExtraction", 1), ("PanProviderCheck", 2)]);
        pan.Providers.Single().ProviderId.ShouldBe("fake-pan");
        pan.Providers.Single().Configured.ShouldBeTrue();
        modules.Single(m => m.Key == "bank-statement").Providers.ShouldBeEmpty();
    }

    [Fact]
    public async Task Switching_a_module_off_is_audited_hides_it_from_submissions_and_does_not_stop_accepted_work()
    {
        var t = await NewTenantAsync("D5");
        var queued = await AcceptedAsync(await SubmitBankAsync(t));
        var modules = await ReadAsync<List<AdminVerificationModuleDto>>(await App.GetAsync("/api/v1/admin/verifications/modules", Platform.AccessToken));
        var bank = modules.Single(m => m.Key == "bank-statement");

        (await App.PutAsync("/api/v1/admin/verifications/modules/bank-statement", new UpdateVerificationModuleRequest(false, "", bank.RowVersion!), Platform.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest); // a reason is required
        (await App.PutAsync("/api/v1/admin/verifications/modules/aadhaar", new UpdateVerificationModuleRequest(false, "no", "AAAA"), Platform.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound); // Upcoming modules cannot be configured
        (await App.PutAsync("/api/v1/admin/verifications/modules/bank-statement", new UpdateVerificationModuleRequest(false, "maintenance", "AAAA"), Platform.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict); // stale / wrong version

        var off = await App.PutAsync("/api/v1/admin/verifications/modules/bank-statement", new UpdateVerificationModuleRequest(false, "maintenance window", bank.RowVersion!), Platform.AccessToken);
        off.StatusCode.ShouldBe(HttpStatusCode.OK, await off.Content.ReadAsStringAsync());
        var updated = await ReadAsync<AdminVerificationModuleDto>(off);
        updated.IsEnabled.ShouldBe(false);
        updated.Status.ShouldBe("Disabled");

        var blocked = await SubmitBankAsync(t, BankPdf("a different synthetic line"));
        blocked.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(blocked)).ShouldBe(ErrorCodes.VerificationModuleDisabled);
        var catalogue = await ReadAsync<List<VerificationModuleDto>>(await App.GetAsync("/api/v1/verifications/modules", t.Token));
        catalogue.Single(m => m.Key == "bank-statement").Status.ShouldBe("Disabled");

        await RunWorkerAsync(); // already accepted work still completes
        (await StatusAsync(t, queued.RequestId)).Status.ShouldBe("Completed");

        var on = await App.PutAsync("/api/v1/admin/verifications/modules/bank-statement", new UpdateVerificationModuleRequest(true, "done", updated.RowVersion!), Platform.AccessToken);
        on.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SubmitBankAsync(t, BankPdf("yet another synthetic line"))).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var audit = await ReadAsync<PagedResult<AdminVerificationAuditItemDto>>(await App.GetAsync("/api/v1/admin/verifications/audit", Platform.AccessToken));
        audit.Items.Select(a => a.Action).ShouldContain("verification.module_disabled");
        audit.Items.Select(a => a.Action).ShouldContain("verification.module_enabled");
        audit.Items.ShouldAllBe(a => a.Action.StartsWith("verification.", StringComparison.Ordinal));
        var reason = await App.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.Action == "verification.module_disabled").Select(a => a.NewValuesJson).SingleAsync());
        reason!.ShouldContain("maintenance window");
    }

    [Fact]
    public async Task Stats_and_tenant_investigation_expose_metadata_only_and_every_view_is_audited()
    {
        var a = await NewTenantAsync("D6A");
        var b = await NewTenantAsync("D6B");
        var bank = await AcceptedAsync(await SubmitBankAsync(a));
        await AcceptedAsync(await SubmitPanAsync(b, PanPdf("ABCPV1234V"), PanFields("ABCPV1234V")));
        await RunWorkerAsync();

        var stats = await ReadAsync<VerificationStatsDto>(await App.GetAsync("/api/v1/admin/verifications/stats", Platform.AccessToken));
        stats.Total.ShouldBe(2);
        stats.ByType.Select(c => c.Key).Order().ShouldBe(["BankStatement", "Pan"]);
        stats.ExtractionCreditsCharged.ShouldBe(5 + 1);
        stats.ProviderCreditsCharged.ShouldBe(2);
        stats.ProviderOutcomes.Single().Key.ShouldBe("Verified");
        var onlyA = await ReadAsync<VerificationStatsDto>(await App.GetAsync($"/api/v1/admin/verifications/stats?clientId={a.ClientId}", Platform.AccessToken));
        onlyA.Total.ShouldBe(1);

        var csv = await App.GetAsync("/api/v1/admin/verifications/stats/export.csv", Platform.AccessToken);
        csv.StatusCode.ShouldBe(HttpStatusCode.OK);
        csv.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        (await csv.Content.ReadAsStringAsync()).ShouldContain("date,type,total");

        var list = await App.GetAsync($"/api/v1/admin/clients/{a.ClientId}/verifications", Platform.AccessToken);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        var listText = await list.Content.ReadAsStringAsync();
        listText.ShouldContain(bank.RequestId.ToString());
        foreach (var forbidden in new[] { "masked", "\"bankStatement\":", "\"transactions\":", "reviewReasons", "externalRef", SampleTexts.Holder, "9900112233" })
        {
            listText.ShouldNotContain(forbidden, Case.Insensitive);
        }

        var detail = await App.GetAsync($"/api/v1/admin/clients/{a.ClientId}/verifications/{bank.RequestId}", Platform.AccessToken);
        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detailBody = await ReadAsync<AdminVerificationDetailDto>(detail);
        detailBody.Events.Select(e => e.ToStatus).ShouldBe(["Queued", "Processing", "Completed"]);
        var detailText = await detail.Content.ReadAsStringAsync();
        foreach (var forbidden in new[] { "\"checks\"", "masked", "\"bankStatement\":", "\"transactions\":", SampleTexts.Holder })
        {
            detailText.ShouldNotContain(forbidden, Case.Insensitive);
        }

        // Another client's id through the wrong client route is simply not found; an unknown client is 404.
        (await App.GetAsync($"/api/v1/admin/clients/{b.ClientId}/verifications/{bank.RequestId}", Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await App.GetAsync($"/api/v1/admin/clients/{Guid.NewGuid()}/verifications", Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var audit = await ReadAsync<PagedResult<AdminVerificationAuditItemDto>>(await App.GetAsync("/api/v1/admin/verifications/audit", Platform.AccessToken));
        audit.Items.Count(i => i.Action == "verification.admin_viewed").ShouldBe(1);
    }

    [Fact]
    public async Task Credit_costs_per_verification_operation_use_the_existing_cost_rule_admin()
    {
        var t = await NewTenantAsync("D7");
        (await App.PutAsync("/api/v1/admin/cost-rules/default", new SetCostRuleRequest("PanExtraction", 4, "OnCompleted", null, "pricing review"), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await App.PutAsync($"/api/v1/admin/cost-rules/clients/{t.ClientId}", new SetCostRuleRequest("PanProviderCheck", 7, "OnCompleted", null), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var catalogue = await ReadAsync<List<VerificationModuleDto>>(await App.GetAsync("/api/v1/verifications/modules", t.Token));
        var panModule = catalogue.Single(m => m.Key == "pan");
        (panModule.ExtractionCredits, panModule.ProviderCredits).ShouldBe((4, 7));
        catalogue.Single(m => m.Key == "bank-statement").ProviderCredits.ShouldBeNull();
        catalogue.Where(m => m.Status == "Upcoming").ShouldAllBe(m => m.ExtractionCredits == null);

        var result = await ProcessAsync(t, await SubmitPanAsync(t, PanPdf("ABCPV1234V"), PanFields("ABCPV1234V")));
        result.Credits!.Extraction.Charged.ShouldBe(4);
        result.Credits.Provider.Charged.ShouldBe(7);
        (await ConsumedAsync(t)).ShouldBe(11);

        var modules = await ReadAsync<List<AdminVerificationModuleDto>>(await App.GetAsync("/api/v1/admin/verifications/modules", Platform.AccessToken));
        modules.Single(m => m.Key == "pan").Costs.Single(c => c.Operation == "PanExtraction").Credits.ShouldBe(4);
    }
}
