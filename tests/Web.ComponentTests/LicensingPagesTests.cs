using NexaVerify.Contracts.Licensing;
using LicenseDetailPage = NexaVerify.Web.Pages.Admin.LicenseDetail;
using LicensesPage = NexaVerify.Web.Pages.Admin.Licenses;
using PlansPage = NexaVerify.Web.Pages.Admin.Plans;
using ReportsPage = NexaVerify.Web.Pages.Admin.Reports;

namespace NexaVerify.Web.ComponentTests;

public class LicensingPagesTests : PageTestBase
{
    private IRenderedComponent<LicenseDetailPage> RenderDetail(string status = "Active") =>
        Render<LicenseDetailPage>(p =>
        {
            Licensing.License = () => Sample.License(status);
            p.Add(x => x.Id, Guid.NewGuid());
        });

    [Fact]
    public void The_license_list_shows_credits_status_and_a_link_to_the_detail()
    {
        SignInAsEverything();
        Providers();

        var cut = Render<LicensesPage>();

        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));
        var row = cut.Find("table tbody tr");
        row.TextContent.ShouldContain("842 / 1,000");
        row.QuerySelector(".nv-status-chip")!.GetAttribute("data-status").ShouldBe("Active");
        row.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).ShouldContain(h => h!.StartsWith("admin/licenses/", StringComparison.Ordinal));
    }

    [Fact]
    public void The_expiring_filter_is_passed_to_the_api()
    {
        SignInAsEverything();
        Providers();
        var cut = Render<LicensesPage>();
        cut.WaitForAssertion(() => Licensing.Calls.Count.ShouldBeGreaterThan(0));

        cut.FindComponent<MudSwitch<bool>>().Find("input").Change(true);

        cut.WaitForAssertion(() => Licensing.Calls.ShouldContain("list:::30"));
    }

    [Fact]
    public void Only_people_who_may_issue_licenses_see_the_button()
    {
        SignInAs(WebPermissions.LicensesRead);
        Providers();
        Render<LicensesPage>().FindAll("[data-testid=issue-license]").ShouldBeEmpty();

        SignInAs(WebPermissions.LicensesRead, WebPermissions.LicensesCreate);
        Render<LicensesPage>().FindAll("[data-testid=issue-license]").Count.ShouldBe(1);
    }

    [Fact]
    public void License_actions_follow_permissions_and_status()
    {
        SignInAs(WebPermissions.LicensesRead);
        Providers();
        var readOnly = RenderDetail();
        readOnly.WaitForAssertion(() => readOnly.Markup.ShouldContain("Annual 2026"));
        readOnly.FindAll("[data-testid=renew-license],[data-testid=adjust-license],[data-testid=suspend-license],[data-testid=revoke-license]").ShouldBeEmpty();

        SignInAsEverything();
        var full = RenderDetail();
        full.WaitForAssertion(() => full.FindAll("[data-testid=suspend-license]").Count.ShouldBe(1));
        full.FindAll("[data-testid=renew-license],[data-testid=adjust-license],[data-testid=revoke-license]").Count.ShouldBe(3);
        full.FindAll("[data-testid=activate-license]").ShouldBeEmpty("already active");

        var suspended = RenderDetail("Suspended");
        suspended.WaitForAssertion(() => suspended.FindAll("[data-testid=activate-license]").Count.ShouldBe(1));
        suspended.FindAll("[data-testid=suspend-license]").ShouldBeEmpty();

        var revoked = RenderDetail("Revoked");
        revoked.WaitForAssertion(() => revoked.Markup.ShouldContain("Annual 2026"));
        revoked.FindAll("[data-testid=renew-license],[data-testid=adjust-license],[data-testid=revoke-license],[data-testid=activate-license]").ShouldBeEmpty();
    }

    [Fact]
    public void License_notes_are_shown_as_text_not_markup()
    {
        SignInAsEverything();
        Providers();

        var cut = RenderDetail();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("&lt;b&gt;notes&lt;/b&gt;"));
        cut.FindAll("[data-testid=detail-list] b").ShouldBeEmpty();
    }

    [Fact]
    public void Adjusting_credits_needs_a_reason_and_a_non_zero_amount_and_then_calls_the_api()
    {
        SignInAsEverything();
        var providers = Providers();
        var cut = RenderDetail();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=adjust-license]").Count.ShouldBe(1));

        cut.Find("[data-testid=adjust-license]").Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));
        providers.Find("[data-testid=form-submit]").Click();

        providers.WaitForAssertion(() => providers.Markup.ShouldContain("Explain why the credits are changing"));
        Licensing.Calls.ShouldNotContain(c => c.StartsWith("adjust", StringComparison.Ordinal));

        var numeric = providers.FindComponent<MudNumericField<int>>();
        numeric.Find("input").Change("-5");
        providers.Find(".mud-dialog textarea").Input("Chargeback for duplicate test run");
        providers.Find("[data-testid=form-submit]").Click();

        cut.WaitForAssertion(() => Licensing.Calls.ShouldContain("adjust:-5:Chargeback for duplicate test run"));
    }

    [Fact]
    public void Revoking_needs_the_typed_word_and_a_reason()
    {
        SignInAsEverything();
        var providers = Providers();
        var cut = RenderDetail();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=revoke-license]").Count.ShouldBe(1));

        cut.Find("[data-testid=revoke-license]").Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=confirm-ok]").Count.ShouldBe(1));
        providers.Find("[data-testid=confirm-phrase]").TextContent.ShouldBe("REVOKE");
        providers.Find("[data-testid=confirm-ok]").HasAttribute("disabled").ShouldBeTrue();
        providers.Find(".mud-dialog textarea").Input("fraud");
        providers.Find("[data-testid=confirm-ok]").HasAttribute("disabled").ShouldBeTrue("still needs the typed word");
        providers.Find(".mud-dialog input").Input("REVOKE");
        providers.Find("[data-testid=confirm-ok]").Click();

        cut.WaitForAssertion(() => Licensing.Calls.ShouldContain("revoke:fraud"));
    }

    [Fact]
    public void Plans_can_only_be_created_by_people_who_manage_them()
    {
        SignInAs(WebPermissions.LicensesRead);
        Providers();
        var readOnly = Render<PlansPage>();
        readOnly.WaitForAssertion(() => readOnly.FindAll("table tbody tr").Count.ShouldBe(1));
        readOnly.FindAll("[data-testid=new-plan]").ShouldBeEmpty();
        readOnly.FindAll("table tbody tr button").ShouldBeEmpty();

        SignInAs(WebPermissions.LicensesRead, WebPermissions.PlansManage);
        var manager = Render<PlansPage>();
        manager.WaitForAssertion(() => manager.FindAll("[data-testid=new-plan]").Count.ShouldBe(1));
        manager.WaitForAssertion(() => manager.FindAll("table tbody tr button").Count.ShouldBe(1));
    }

    [Fact]
    public void Plan_errors_use_the_standard_error_state()
    {
        SignInAsEverything();
        Providers();
        Licensing.Plans = () => Task.FromResult(ApiResult<IReadOnlyList<PlanDto>>.Fail("FORBIDDEN", "You don't have permission to do this.", "corr-12", 403));

        var cut = Render<PlansPage>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-12"));
    }

    [Fact]
    public void Reports_offer_a_download_link_through_the_portal_and_never_a_token()
    {
        SignInAsEverything();
        Providers();

        var cut = Render<ReportsPage>();

        var href = cut.Find("[data-testid=download-csv]").GetAttribute("href")!;
        href.ShouldStartWith("bff/reports/usage.csv?from=2026-05-17&to=2026-06-15");
        href.ShouldNotContain("token");
        cut.Markup.ShouldNotContain("Bearer");
    }

    [Fact]
    public async Task A_report_range_longer_than_the_api_allows_disables_the_download()
    {
        SignInAsEverything();
        Providers();
        var cut = Render<ReportsPage>();

        var pickers = cut.FindComponents<MudDatePicker>();
        await cut.InvokeAsync(() => pickers[0].Instance.DateChanged.InvokeAsync(new DateTime(2026, 1, 1)));

        cut.Find("[data-testid=range-problem]").TextContent.ShouldContain("at most 92 days");
        cut.Find("[data-testid=download-csv]").HasAttribute("disabled").ShouldBeTrue();
    }

    [Fact]
    public void The_ledger_check_button_is_only_for_people_who_may_run_it()
    {
        SignInAs(WebPermissions.ReportsRead);
        Providers();

        Render<ReportsPage>().FindAll("[data-testid=verify-ledgers]").ShouldBeEmpty();
    }

    [Fact]
    public void A_clean_ledger_check_says_all_clear()
    {
        SignInAsEverything();
        Providers();
        var cut = Render<ReportsPage>();

        cut.Find("[data-testid=verify-ledgers]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=verify-ok]").TextContent.ShouldContain("3 licenses and 40 credit entries"));
        Licensing.Calls.ShouldContain("verify-all");
    }

    [Fact]
    public void A_broken_ledger_is_listed_with_a_link_to_the_license()
    {
        SignInAsEverything();
        Providers();
        var licenseId = Guid.NewGuid();
        Licensing.VerifyAll = () => Task.FromResult(ApiResult<LedgerVerificationReportDto>.Ok(new LedgerVerificationReportDto(
            Sample.Now, Sample.Now, 3, 40, 1, [new LedgerBreakDto(licenseId, Guid.NewGuid(), 17, "Hash chain broken at entry 17.")])));
        var cut = Render<ReportsPage>();

        cut.Find("[data-testid=verify-ledgers]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=verify-broken]").TextContent.ShouldContain("1 of 3 licenses failed"));
        var row = cut.Find("[data-testid=verify-result] tbody tr");
        row.TextContent.ShouldContain("17");
        row.TextContent.ShouldContain("Hash chain broken at entry 17.");
        row.QuerySelector("a")!.GetAttribute("href").ShouldBe($"admin/licenses/{licenseId}");
    }

    [Fact]
    public void A_ledger_check_that_is_already_running_is_reported_with_retry()
    {
        SignInAsEverything();
        Providers();
        Licensing.VerifyAll = () => Task.FromResult(ApiResult<LedgerVerificationReportDto>.Fail("CONFLICT", "A ledger check is already running.", "corr-31", 409));
        var cut = Render<ReportsPage>();

        cut.Find("[data-testid=verify-ledgers]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=verify-result] [data-testid=correlation-id]").TextContent.ShouldBe("corr-31"));
        cut.Find("[data-testid=verify-result]").TextContent.ShouldContain("already running");
    }
}
