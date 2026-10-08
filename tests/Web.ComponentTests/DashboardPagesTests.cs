using NexaVerify.Contracts.Dashboards;
using NexaVerify.Contracts.Licensing;
using AdminDashboardPage = NexaVerify.Web.Pages.Admin.AdminDashboard;
using ClientDashboardPage = NexaVerify.Web.Pages.Client.ClientDashboard;

namespace NexaVerify.Web.ComponentTests;

public class DashboardPagesTests : PageTestBase
{
    [Fact]
    public void The_admin_dashboard_shows_a_skeleton_then_the_figures()
    {
        SignInAsEverything();
        Providers();
        var gate = new Gate<ApiResult<AdminDashboardModel>>();
        Dashboard.Admin = _ => gate.Task;

        var cut = Render<AdminDashboardPage>();

        cut.FindAll("[data-testid=dashboard-skeleton]").Count.ShouldBe(1);
        gate.Release(ApiResult<AdminDashboardModel>.Ok(Sample.AdminDashboard()));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=stat-value]").Select(v => v.TextContent).ShouldBe(["128", "12,480"]));
        cut.Markup.ShouldContain("1 license(s) expire soon.");
        cut.FindAll("[data-testid=clients-by-status] .nv-status-chip").Select(c => c.GetAttribute("data-status")).ShouldBe(["Active", "Suspended"]);
    }

    [Fact]
    public void The_admin_dashboard_error_state_has_retry_and_the_reference()
    {
        SignInAsEverything();
        Providers();
        var attempts = 0;
        Dashboard.Admin = _ => Task.FromResult(++attempts == 1
            ? ApiResult<AdminDashboardModel>.Fail("INTERNAL_ERROR", "Something went wrong on our side. Please try again.", "corr-dash", 500)
            : ApiResult<AdminDashboardModel>.Ok(Sample.AdminDashboard()));

        var cut = Render<AdminDashboardPage>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-dash"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Try again")).Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=stat-value]").Count.ShouldBe(2));
    }

    [Fact]
    public void Changing_the_time_range_reloads_with_the_matching_number_of_days()
    {
        SignInAsEverything();
        Providers();
        var cut = Render<AdminDashboardPage>();
        cut.WaitForAssertion(() => Dashboard.AdminRanges.Count.ShouldBe(1));
        Dashboard.AdminRanges[0].ShouldBe(DashboardRange.Last30Days);

        cut.FindAll("[data-testid=range-selector] button").Single(b => b.TextContent.Trim() == "7 days").Click();

        cut.WaitForAssertion(() => Dashboard.AdminRanges.ShouldBe([DashboardRange.Last30Days, DashboardRange.Last7Days]));
    }

    [Fact]
    public void An_admin_dashboard_without_data_shows_empty_charts_instead_of_breaking()
    {
        SignInAsEverything();
        Providers();
        Dashboard.Admin = _ => Task.FromResult(ApiResult<AdminDashboardModel>.Ok(Sample.AdminDashboard() with { RequestsTrend = [], CreditsTrend = [], ClientUsage = [], ExpiringLicenses = [], Alerts = [] }));

        var cut = Render<AdminDashboardPage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=stat-value]").Count.ShouldBe(2));
        cut.Markup.ShouldContain("Nothing needs your attention.");
        cut.Markup.ShouldContain("No licenses are close to expiring.");
    }

    [Fact]
    public void The_client_dashboard_speaks_plainly_and_shows_the_account_card_from_the_signed_in_user()
    {
        User.SignIn("Una User", "una@acme.test", "ClientAdmin", "Acme Corp", WebPermissions.ClientAdminDefaults);
        Providers();

        var cut = Render<ClientDashboardPage>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Acme Corp"));
        cut.Markup.ShouldContain("Business plan");
        cut.Markup.ShouldContain("842 credits available.");
        cut.Markup.ShouldNotContain("ledger");
        cut.Markup.ShouldContain("—"); // no latency yet
    }

    [Fact]
    public void The_client_dashboard_shows_the_error_state()
    {
        SignInAsEverything();
        Providers();
        Dashboard.Client = _ => Task.FromResult(ApiResult<ClientDashboardModel>.Fail("API_UNAVAILABLE", "We can't reach the service right now. Please try again in a moment.", null, 503));

        var cut = Render<ClientDashboardPage>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("We can't reach the service right now."));
    }
}

public class DashboardMapperTests
{
    private static readonly DateTime Today = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Admin_figures_come_straight_from_the_api_numbers()
    {
        var dto = new AdminDashboardDto(
            Today.AddDays(-6), Today, 7,
            [new CountByLabelDto("Active", 12), new CountByLabelDto("Suspended", 1)], [new CountByLabelDto("Active", 20)],
            new LicenseAttentionListDto(30, 1, [new LicenseAttentionDto(Guid.NewGuid(), Guid.NewGuid(), "Initech", "Annual", 1200, 50000, 2, Today.AddDays(4))]),
            new LicenseAttentionListDto(20, 0, []),
            new CreditSummaryDto(900, 100, 800),
            [new CreditDayDto(DateOnly.FromDateTime(Today), 10, 2, 8)],
            [], [new TopClientDto(Guid.NewGuid(), "ACME", "Acme Corp", 500, 480)],
            new ApiSummaryDto(1000, 50, 5, 0.05m, 320),
            [new ApiDayDto(DateOnly.FromDateTime(Today), 1000, 50, 5, 320)],
            new WebhookHealthDto(2, 1, 40, 1, 0));

        var model = DashboardMapper.ToModel(dto);

        model.Kpis.Select(k => k.Value).ShouldBe(["12", "800", "1,000", "5.0%", "1", "0"]);
        model.ClientUsage.Single().ShouldBe(new ClientUsageRow("Acme Corp", 480, 500));
        model.ExpiringLicenses.Single().Remaining.ShouldBe(1200);
        model.RequestsTrend[0].Points.Single().Value.ShouldBe(950);
        model.RequestsTrend[1].Points.Single().Value.ShouldBe(50);
        model.Alerts.Select(a => a.Severity).ShouldBe(["warning", "warning", "warning", "error"]);
        model.Alerts.ShouldContain(a => a.Message.Contains("1 webhook endpoint(s) are failing"));
    }

    [Fact]
    public void Client_figures_use_plain_language_and_group_checks_by_type()
    {
        var licenses = new LicenseSummaryDto("Low", "Only 90 credits remain (9%).", 90, 1000, 910, 9, Today.AddDays(5), 5, 1,
            [new LicenseListItemDto(Guid.NewGuid(), Guid.NewGuid(), "Acme", "K", "Annual", "Business", "Active", 1000, 910, 90, Today.AddDays(-300), Today.AddDays(5), 5)]);
        var d1 = DateOnly.FromDateTime(Today.AddDays(-1));
        var d2 = DateOnly.FromDateTime(Today);
        var dto = new ClientDashboardDto(
            Today.AddDays(-1), Today, 2, licenses,
            new RecognitionSummaryDto(10, 7, 2, 1, 0.7m, 0.2m, 0.1m),
            [new RecognitionDayDto(d1, 4, 3, 1, 0), new RecognitionDayDto(d2, 6, 4, 1, 1)],
            [new RecognitionBreakdownDto(d1, "Verify", "Matched", 3), new RecognitionBreakdownDto(d2, "Identify", "NoMatch", 1)],
            new CreditSummaryDto(12, 2, 10), [], new ApiSummaryDto(100, 2, 0, 0.02m, null), [], []);

        var model = DashboardMapper.ToModel(dto);

        model.CreditsRemaining.ShouldBe(90);
        model.PlanName.ShouldBe("Business");
        model.Outcomes.ShouldBe(new OutcomeSplit(7, 2, 1));
        model.UsageTrend.Select(s => s.Name).ShouldBe(["Identify", "Verify"]);
        model.UsageTrend.Single(s => s.Name == "Verify").Points.Select(p => p.Value).ShouldBe([3, 0]);
        model.ApiUsage.ErrorRatePercent.ShouldBe(2.0);
        model.ApiUsage.P95LatencyMs.ShouldBeNull();
        model.Alerts.Single().ShouldBe(new AlertModel("warning", "Only 90 credits remain (9%)."));
        model.LicenseExpiresAt.ShouldBe(new DateTimeOffset(Today.AddDays(5), TimeSpan.Zero));
    }

    [Fact]
    public void A_healthy_license_raises_no_alert()
    {
        var licenses = new LicenseSummaryDto("Healthy", "842 credits available.", 842, 1000, 158, 84, Today.AddDays(40), 40, 1, []);
        var dto = new ClientDashboardDto(Today, Today, 1, licenses, new RecognitionSummaryDto(0, 0, 0, 0, 0, 0, 0), [], [], new CreditSummaryDto(0, 0, 0), [], new ApiSummaryDto(0, 0, 0, 0, null), [], []);

        DashboardMapper.ToModel(dto).Alerts.ShouldBeEmpty();
    }
}
