using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Tenancy;
using AuditPage = NexaVerify.Web.Pages.Admin.Verification.VerificationAudit;
using InvestigationDetailPage = NexaVerify.Web.Pages.Admin.Verification.VerificationInvestigationDetail;
using InvestigationPage = NexaVerify.Web.Pages.Admin.Verification.VerificationInvestigation;
using ModulesPage = NexaVerify.Web.Pages.Admin.Verification.VerificationModules;
using OperationsPage = NexaVerify.Web.Pages.Admin.Verification.VerificationOperations;

namespace NexaVerify.Web.ComponentTests;

public class VerificationAdminModulesTests : VerificationPageTestBase
{
    private void SignInStaff(params string[] without) =>
        SignInAs(new[] { WebPermissions.VerificationAdminRead, WebPermissions.VerificationAdminConfigure, WebPermissions.LicensesCostRules, WebPermissions.AuditRead }.Where(p => !without.Contains(p)).ToArray());

    private IRenderedComponent<ModulesPage> RenderReady()
    {
        var cut = Render<ModulesPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=admin-module]").Count.ShouldBe(2));
        return cut;
    }

    [Fact]
    public void Shows_a_skeleton_then_modules_with_providers_costs_and_muted_upcoming_rows_without_actions()
    {
        SignInStaff();
        var gate = new Gate<ApiResult<IReadOnlyList<AdminVerificationModuleDto>>>();
        AdminApi.Modules = () => gate.Task;
        var cut = Render<ModulesPage>();
        cut.FindAll("[data-testid=skeleton-card]").Count.ShouldBeGreaterThan(0);

        gate.Release(ApiResult<IReadOnlyList<AdminVerificationModuleDto>>.Ok([VerificationSample.AdminModule("bank-statement"), VerificationSample.AdminModule(), VerificationSample.AdminUpcoming()]));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=admin-module]").Count.ShouldBe(2));
        var pan = cut.FindAll("[data-testid=admin-module]").Single(m => m.GetAttribute("data-module") == "pan");
        pan.QuerySelector("[data-testid=provider-configured]")!.TextContent.ShouldBe("Not set up");
        pan.TextContent.ShouldContain("3 verified");
        pan.QuerySelectorAll("[data-testid=cost-row]").Count.ShouldBe(2);
        pan.TextContent.ShouldContain("1 credit");
        var upcoming = cut.Find("[data-testid=admin-upcoming]");
        upcoming.QuerySelectorAll("button, a").ShouldBeEmpty();
        upcoming.TextContent.ShouldNotContain("credit", Case.Insensitive);
        cut.FindAll("h1").Count.ShouldBe(1);
    }

    [Fact]
    public void Provider_secrets_never_appear_only_whether_a_provider_is_set_up()
    {
        SignInStaff();

        var cut = RenderReady();

        cut.Find("[data-testid=modules-note]").TextContent.ShouldContain("never shown here");
        cut.Markup.ShouldNotContain("apikey", Case.Insensitive);
        cut.Markup.ShouldNotContain("secret", Case.Insensitive);
    }

    [Fact]
    public void Switching_off_needs_a_reason_and_sends_the_row_version()
    {
        SignInStaff();
        var providers = Providers();
        var cut = RenderReady();

        cut.FindAll("[data-testid=toggle-module]")[1].Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=confirm-ok]").Count.ShouldBe(1));
        providers.Find("[data-testid=confirm-ok]").HasAttribute("disabled").ShouldBeTrue("a reason is required");
        providers.FindComponent<MudTextField<string>>().Find("textarea").Input("Provider contract ended");
        providers.Find("[data-testid=confirm-ok]").Click();

        cut.WaitForAssertion(() => AdminApi.LastUpdate.ShouldNotBeNull());
        AdminApi.LastUpdate!.IsEnabled.ShouldBeFalse();
        AdminApi.LastUpdate.Reason.ShouldBe("Provider contract ended");
        AdminApi.LastUpdate.RowVersion.ShouldBe("RV1");
        cut.WaitForAssertion(() => AdminApi.Calls.Count(c => c == "modules").ShouldBe(2));
    }

    [Fact]
    public void A_conflicting_change_is_reported_and_nothing_else_happens()
    {
        SignInStaff();
        var providers = Providers();
        AdminApi.Update = () => Ok.Fail<bool>("CONCURRENCY_CONFLICT", "Someone else changed this at the same time. Reload and try again.", "corr-cc", 409);
        var cut = RenderReady();

        cut.FindAll("[data-testid=toggle-module]")[0].Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=confirm-ok]").Count.ShouldBe(1));
        providers.FindComponent<MudTextField<string>>().Find("textarea").Input("x");
        providers.Find("[data-testid=confirm-ok]").Click();

        cut.WaitForAssertion(() => AdminApi.Calls.ShouldContain("update:bank-statement"));
        AdminApi.Calls.Count(c => c == "modules").ShouldBe(1, "no reload after a failed change");
    }

    [Fact]
    public void Editing_a_price_validates_and_sends_the_credits_with_the_audit_note()
    {
        SignInStaff();
        var providers = Providers();
        var cut = RenderReady();

        cut.FindAll("[data-testid=edit-cost]")[0].Click();
        providers.WaitForAssertion(() => providers.FindAll("input").Count.ShouldBeGreaterThan(0));
        providers.FindComponent<MudNumericField<int>>().Find("input").Change("4");
        providers.Find("form").Submit();
        providers.WaitForAssertion(() => providers.Markup.ShouldContain("Say why the price is changing"));
        AdminApi.LastCost.ShouldBeNull();

        providers.FindComponents<MudTextField<string>>().First(f => f.Instance.Label == "Reason (kept in the audit log)").Find("textarea").Input("Pricing review");
        providers.Find("form").Submit();

        cut.WaitForAssertion(() => AdminApi.LastCost.ShouldNotBeNull());
        AdminApi.LastCost!.Value.Operation.ShouldBe("PanExtraction");
        AdminApi.LastCost.Value.Credits.ShouldBe(4);
        AdminApi.LastCostReason.ShouldBe("Pricing review");
    }

    [Fact]
    public void Without_the_configure_and_cost_permissions_there_are_no_buttons()
    {
        SignInStaff(WebPermissions.VerificationAdminConfigure, WebPermissions.LicensesCostRules);

        var cut = RenderReady();

        cut.FindAll("[data-testid=toggle-module]").ShouldBeEmpty();
        cut.FindAll("[data-testid=edit-cost]").ShouldBeEmpty();
    }

    [Fact]
    public void An_error_offers_retry()
    {
        SignInStaff();
        var attempts = 0;
        AdminApi.Modules = () => ++attempts == 1 ? Ok.Fail<IReadOnlyList<AdminVerificationModuleDto>>(correlation: "corr-m") : Ok.Of<IReadOnlyList<AdminVerificationModuleDto>>([VerificationSample.AdminModule()]);

        var cut = Render<ModulesPage>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-m"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Try again")).Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=admin-module]").Count.ShouldBe(1));
    }
}

public class VerificationAdminOperationsTests : VerificationPageTestBase
{
    [Fact]
    public void Shows_volume_results_provider_problems_and_credits()
    {
        SignInAs(WebPermissions.VerificationAdminRead);

        var cut = Render<OperationsPage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=stat-value]").Count.ShouldBeGreaterThan(5));
        var values = cut.FindAll("[data-testid=stat-value]").Select(v => v.TextContent).ToList();
        values.ShouldContain("100");
        values.ShouldContain("80");
        values.ShouldContain("8");
        values.ShouldContain("12 (12%)");
        values.ShouldContain("4");
        values.ShouldContain("320");
        cut.Markup.ShouldContain("Oldest has waited 65 s");
        cut.FindAll("h1").Count.ShouldBe(1);
        cut.FindAll("[data-testid=chart-body], [data-testid=simple-chart]").Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void The_type_filter_and_time_range_reach_the_api()
    {
        SignInAs(WebPermissions.VerificationAdminRead);
        var cut = Render<OperationsPage>();
        cut.WaitForAssertion(() => AdminApi.LastStatsQuery.ShouldNotBeNull());
        (AdminApi.LastStatsQuery!.To!.Value - AdminApi.LastStatsQuery.From!.Value).TotalDays.ShouldBe(30, 0.01);

        cut.InvokeAsync(() => cut.FindComponents<MudSelect<string>>().First(s => s.Instance.Label == "Type").Instance.ValueChanged.InvokeAsync("Passport"));
        cut.WaitForAssertion(() => AdminApi.LastStatsQuery!.Type.ShouldBe("Passport"));
        cut.Find("[data-testid=range-selector] button:nth-of-type(1)").Click();
        cut.WaitForAssertion(() => (AdminApi.LastStatsQuery!.To!.Value - AdminApi.LastStatsQuery.From!.Value).TotalDays.ShouldBe(7, 0.01));
    }

    [Fact]
    public void No_checks_is_an_empty_state_and_an_error_offers_retry()
    {
        SignInAs(WebPermissions.VerificationAdminRead);
        var attempts = 0;
        AdminApi.Stats = () => ++attempts == 1 ? Ok.Fail<VerificationStatsDto>(correlation: "corr-s") : Ok.Of(VerificationSample.Stats() with { Total = 0 });

        var cut = Render<OperationsPage>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-s"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Try again")).Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=empty-state]").TextContent.ShouldContain("No document checks in this period"));
    }
}

public class VerificationAdminInvestigationTests : VerificationPageTestBase
{
    [Fact]
    public void Asks_for_a_client_first_and_says_staff_cannot_see_documents()
    {
        SignInAs(WebPermissions.VerificationAdminRead);

        var cut = Render<InvestigationPage>();

        cut.Find("[data-testid=empty-state]").TextContent.ShouldContain("Pick a client");
        cut.Find("[data-testid=privacy-banner]").TextContent.ShouldContain("not visible to platform staff");
        AdminApi.Calls.ShouldNotContain("list");
        cut.FindAll("h1").Count.ShouldBe(1);
    }

    [Fact]
    public void The_detail_page_shows_metadata_and_a_timeline_but_no_personal_or_document_data()
    {
        SignInAs(WebPermissions.VerificationAdminRead);
        var client = Guid.NewGuid();

        var cut = Render<InvestigationDetailPage>(p => p.Add(x => x.ClientId, client).Add(x => x.Id, VerificationSample.Id));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=timeline] li").Count.ShouldBe(2));
        cut.Find("[data-testid=privacy-banner]").TextContent.ShouldContain("recorded in the audit log");
        cut.Markup.ShouldContain("DOCUMENT_UNREADABLE");
        cut.Markup.ShouldNotContain("XXXXX", Case.Sensitive);
        cut.FindAll("h1").Count.ShouldBe(1);
    }
}

public class VerificationAdminAuditTests : VerificationPageTestBase
{
    [Fact]
    public void Lists_module_and_price_changes()
    {
        SignInAs(WebPermissions.AuditRead);

        var cut = Render<AuditPage>();

        cut.WaitForAssertion(() => cut.Find("table tbody").TextContent.ShouldContain("verification.module_disabled"));
        cut.FindAll("h1").Count.ShouldBe(1);
    }
}
