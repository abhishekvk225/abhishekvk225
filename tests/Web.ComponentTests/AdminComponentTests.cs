using NexaVerify.Contracts.Identity;
using NexaVerify.Web.Security;

namespace NexaVerify.Web.ComponentTests;

public class AdminComponentTests : PageTestBase
{
    [Fact]
    public void Can_shows_content_only_to_users_with_the_permission()
    {
        SignInAs("clients.read");

        Render<Can>(p => p.Add(x => x.Permission, "clients.read").AddChildContent("<b id='yes'>ok</b>")).FindAll("#yes").Count.ShouldBe(1);
        Render<Can>(p => p.Add(x => x.Permission, "clients.create").AddChildContent("<b id='yes'>ok</b>")).FindAll("#yes").ShouldBeEmpty();
        Render<Can>(p => p.Add(x => x.AnyOf, new[] { "x.y", "clients.read" }).AddChildContent("<b id='yes'>ok</b>")).FindAll("#yes").Count.ShouldBe(1);
    }

    [Fact]
    public void Can_renders_the_fallback_when_not_allowed()
    {
        SignInAs();

        var cut = Render<Can>(p => p.Add(x => x.Permission, "clients.read").AddChildContent("<b id='yes'>ok</b>").Add(x => x.NotAllowed, "<i id='no'>nope</i>"));

        cut.FindAll("#yes").ShouldBeEmpty();
        cut.FindAll("#no").Count.ShouldBe(1);
    }

    [Fact]
    public void Detail_lists_render_api_text_as_text_never_as_markup()
    {
        var items = new[] { new DetailItem("Notes", "<img src=x onerror=alert(1)><script>alert(2)</script>"), new DetailItem("Empty", null) };

        var cut = Render<DetailList>(p => p.Add(x => x.Items, items));

        cut.FindAll("img").ShouldBeEmpty();
        cut.FindAll("script").ShouldBeEmpty();
        cut.Markup.ShouldContain("&lt;img src=x onerror=alert(1)&gt;");
        cut.FindAll("dd")[1].TextContent.Trim().ShouldBe("—");
    }

    [Fact]
    public void The_permission_matrix_shows_granted_and_missing_permissions_in_text_as_well_as_icons()
    {
        var roles = new[] { new RoleDto(Guid.NewGuid(), "Support", "Platform", false, null, ["clients.read"]) };
        var permissions = new[]
        {
            new PermissionDto(Guid.NewGuid(), "clients.read", "Clients", PermissionScopeKind.Platform, "View clients"),
            new PermissionDto(Guid.NewGuid(), "clients.create", "Clients", PermissionScopeKind.Platform, "Create clients"),
        };

        var cut = Render<PermissionMatrix>(p => p.Add(x => x.Roles, roles).Add(x => x.Permissions, permissions));

        var cells = cut.FindAll("td[data-granted]");
        cells.Count.ShouldBe(2);
        cells[0].GetAttribute("data-granted").ShouldBe("true");
        cells[0].TextContent.ShouldContain("Granted");
        cells[1].TextContent.ShouldContain("Not granted");
        cut.FindAll("th[scope=col]").Select(h => h.TextContent).ShouldBe(["Permission", "Support"]);
        cut.FindAll("th[scope=rowgroup]").Single().TextContent.ShouldBe("Clients");
    }

    [Fact]
    public void A_dead_session_offers_sign_in_instead_of_retry()
    {
        var cut = Render<ErrorState>(p => p
            .Add(x => x.Error, new ApiError("SESSION_EXPIRED", "Your session has ended. Please sign in again.", null, 401))
            .Add(x => x.OnRetry, () => { }));

        cut.FindAll("[data-testid=sign-in-again]").Count.ShouldBe(1);
        cut.FindAll("button").Where(b => b.TextContent.Contains("Try again")).ShouldBeEmpty();
    }

    [Fact]
    public void Ordinary_errors_still_offer_retry_and_show_the_reference()
    {
        var cut = Render<ErrorState>(p => p
            .Add(x => x.Error, new ApiError("INTERNAL_ERROR", "Something went wrong on our side. Please try again.", "corr-77", 500))
            .Add(x => x.OnRetry, () => { }));

        cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-77");
        cut.Markup.ShouldContain("Try again");
        cut.FindAll("[data-testid=sign-in-again]").ShouldBeEmpty();
    }

    [Fact]
    public void Access_denied_points_users_back_to_their_own_portal()
    {
        var session = SessionFixtures.NewSession(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(SessionFixtures.Start), portal: PortalKinds.Client);
        Services.AddSingleton<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(FakeAuthState.For(session));

        var denied = Render<CascadingAuthenticationState>(p => p.AddChildContent<AccessDenied>());

        denied.Find("[data-testid=access-denied]").TextContent.ShouldContain("don't have access");
        denied.Find("a").GetAttribute("href").ShouldBe("/client");
    }

    [Theory]
    [InlineData("PendingActivation", "Pending")]
    [InlineData("Revoked", "Revoked")]
    [InlineData("Inactive", "Inactive")]
    public void Status_chips_know_the_api_status_names(string value, string label)
    {
        var kind = value == "Revoked" ? StatusKind.License : StatusKind.Client;

        var cut = Render<StatusChip>(p => p.Add(x => x.Kind, kind).Add(x => x.Value, value));

        cut.Find(".nv-status-chip").GetAttribute("data-status").ShouldBe(label);
    }

    [Fact]
    public void Navigation_only_lists_pages_the_user_may_open()
    {
        var limited = NavigationCatalog.Trim(NavigationCatalog.Admin, p => p is WebPermissions.ClientsRead or WebPermissions.ReportsRead);

        limited.SelectMany(g => g.Items).Select(i => i.Href).ShouldBe(["admin/clients", "admin/reports"]);
        NavigationCatalog.Trim(NavigationCatalog.Admin, _ => false).ShouldBeEmpty();
        NavigationCatalog.Admin.SelectMany(g => g.Items).Where(i => i.Implemented).Select(i => i.Href)
            .ShouldBe(["admin", "admin/clients", "admin/licenses", "admin/plans", "admin/cost-rules", "admin/billing/packs", "admin/billing/orders", "admin/reports", "admin/audit", "admin/roles", "admin/platform-users"], ignoreOrder: true);
    }
}
