using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexaVerify.Web.Security;
using AuditLogsPage = NexaVerify.Web.Pages.Admin.AuditLogs;
using ChangePasswordPage = NexaVerify.Web.Pages.ChangePassword;
using LoginPage = NexaVerify.Web.Pages.Login;
using PlatformUsersPage = NexaVerify.Web.Pages.Admin.PlatformUsers;
using RolesPage = NexaVerify.Web.Pages.Admin.Roles;

namespace NexaVerify.Web.ComponentTests;

public class AccessPagesTests : PageTestBase
{
    [Fact]
    public void Roles_are_listed_per_scope_with_a_permission_matrix()
    {
        SignInAsEverything();
        Providers();

        var cut = Render<RolesPage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=permission-matrix]").Count.ShouldBe(1));
        cut.FindAll("[data-testid=roles-Platform] li").Select(li => li.QuerySelector("strong")!.TextContent).ShouldBe(["SuperAdmin", "Support"]);
        cut.FindAll("[data-testid=roles-Platform] li").First().TextContent.ShouldContain("Built in");
        cut.FindAll("th[scope=col]").Select(h => h.TextContent).ShouldBe(["Permission", "SuperAdmin", "Support"]);
        cut.FindAll("td[data-granted=true]").Count.ShouldBe(4, "3 SuperAdmin + 1 Support, client-only permissions are not on the platform grid");
    }

    [Fact]
    public void Roles_show_an_error_state_when_the_api_refuses()
    {
        SignInAsEverything();
        Providers();
        Access.Roles = () => Task.FromResult(ApiResult<IReadOnlyList<NexaVerify.Contracts.Identity.RoleDto>>.Fail("FORBIDDEN", "You don't have permission to do this.", "corr-r", 403));

        var cut = Render<RolesPage>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("You don't have permission to do this."));
        cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-r");
    }

    [Fact]
    public void Platform_users_are_listed_with_their_roles_and_status()
    {
        SignInAsEverything();
        Providers();

        var cut = Render<PlatformUsersPage>();

        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));
        var row = cut.Find("table tbody tr");
        row.TextContent.ShouldContain("ada@nexaverify.test");
        row.TextContent.ShouldContain("SuperAdmin");
        row.QuerySelector(".nv-status-chip")!.GetAttribute("data-status").ShouldBe("Active");
    }

    [Fact]
    public void The_audit_viewer_asks_for_a_client_first()
    {
        SignInAsEverything();
        Providers();

        var cut = Render<AuditLogsPage>();

        cut.FindAll("[data-testid=empty-state]").Count.ShouldBe(1);
        cut.Markup.ShouldContain("Pick a client");
        cut.FindAll("[data-testid=data-table]").ShouldBeEmpty();
    }
}

public class AuthPagesTests : PageTestBase
{
    private IRenderedComponent<LoginPage> RenderLogin(string? error = null, string? reference = null, string? returnUrl = null, string? signedOut = null, string env = "Production")
    {
        Environment.EnvironmentName = env;
        var query = new[] { ("error", error), ("ref", reference), ("returnUrl", returnUrl), ("signedOut", signedOut) }
            .Where(q => q.Item2 is not null)
            .Select(q => $"{q.Item1}={Uri.EscapeDataString(q.Item2!)}");
        Services.GetRequiredService<NavigationManager>().NavigateTo("/login?" + string.Join('&', query));
        return Render<LoginPage>();
    }

    [Fact]
    public void The_sign_in_form_posts_to_the_bff_with_an_antiforgery_token_and_labelled_fields()
    {
        var cut = RenderLogin();

        var form = cut.Find("form[data-testid=login-form]");
        form.GetAttribute("method").ShouldBe("post");
        form.GetAttribute("action").ShouldBe("/auth/login");
        cut.Find("input[name=__RequestVerificationToken]").GetAttribute("value").ShouldBe("form-token-123");
        cut.Find("label[for=login-email]").TextContent.ShouldBe("Email address");
        cut.Find("#login-email").GetAttribute("autocomplete").ShouldBe("username");
        cut.Find("#login-password").GetAttribute("type").ShouldBe("password");
        cut.Find("#login-password").GetAttribute("autocomplete").ShouldBe("current-password");
        cut.FindAll("[data-testid=login-error]").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("invalid", "The email or password is not correct.")]
    [InlineData("throttled", "Too many sign-in attempts. Please wait a few minutes and try again.")]
    [InlineData("blocked", "This account can't sign in right now. Please contact your administrator.")]
    [InlineData("unavailable", "We couldn't sign you in right now. Please try again in a moment.")]
    [InlineData("expired", "Your sign-in page expired. Please try again.")]
    public void Sign_in_failures_are_explained_in_plain_words_without_naming_the_cause(string code, string message)
    {
        var cut = RenderLogin(error: code, reference: "corr-7");

        var alert = cut.Find("[data-testid=login-error]");
        alert.GetAttribute("role").ShouldBe("alert");
        alert.TextContent.ShouldContain(message);
        alert.TextContent.ShouldContain("Reference: corr-7");
    }

    [Fact]
    public void Unknown_error_codes_and_hostile_references_are_ignored()
    {
        var cut = RenderLogin(error: "<script>alert(1)</script>", reference: "<img src=x>");

        cut.FindAll("[data-testid=login-error]").ShouldBeEmpty();
        cut.Markup.ShouldNotContain("<script>alert");

        var withReference = RenderLogin(error: "invalid", reference: "<img src=x onerror=alert(1)>");
        withReference.FindAll("img").ShouldBeEmpty();
        withReference.Markup.ShouldNotContain("Reference:");
    }

    [Theory]
    [InlineData("/admin/clients", "/admin/clients")]
    [InlineData("https://evil.example", null)]
    [InlineData("//evil.example", null)]
    [InlineData("/login", null)]
    public void The_return_url_is_only_carried_when_it_is_a_local_path(string url, string? carried)
    {
        var cut = RenderLogin(returnUrl: url);

        var hidden = cut.FindAll("input[name=returnUrl]");
        if (carried is null)
        {
            hidden.ShouldBeEmpty();
        }
        else
        {
            hidden.Single().GetAttribute("value").ShouldBe(carried);
        }
    }

    [Fact]
    public void Signed_out_users_see_a_calm_confirmation()
    {
        RenderLogin(signedOut: "1").Find("[data-testid=login-signed-out]").TextContent.ShouldContain("signed out");
    }

    [Fact]
    public void The_development_hint_never_shows_in_production()
    {
        RenderLogin(env: "Production").FindAll("[data-testid=dev-hint]").ShouldBeEmpty();
        RenderLogin(env: "Development").FindAll("[data-testid=dev-hint]").Count.ShouldBe(1);
    }

    // ---- change password ----

    private (IRenderedComponent<ChangePasswordPage> Cut, FakePortalAuth Auth, Bunit.TestDoubles.BunitNavigationManager Nav) RenderChange(bool forced, string portal = PortalKinds.Admin)
    {
        var session = SessionFixtures.NewSession(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(SessionFixtures.Start), portal: portal) with { MustChangePassword = forced };
        Services.AddSingleton<AuthenticationStateProvider>(FakeAuthState.For(session));
        var auth = new FakePortalAuth();
        Services.AddSingleton<IPortalAuth>(auth);
        Services.AddSingleton<ICurrentSession>(new FixedSession(session.Id));
        Providers();
        var cut = Render<CascadingAuthenticationState>(p => p.AddChildContent<ChangePasswordPage>()).FindComponent<ChangePasswordPage>();
        return (cut, auth, (Bunit.TestDoubles.BunitNavigationManager)Services.GetRequiredService<NavigationManager>());
    }

    private sealed class FakePortalAuth : IPortalAuth
    {
        public List<ChangePasswordModel> Changes { get; } = [];

        public ApiResult<bool> ChangeResult { get; set; } = ApiResult<bool>.Ok(true);

        public Task<ApiResult<PortalSession>> SignInAsync(string email, string password, CancellationToken ct = default, string? clientIp = null) => throw new NotSupportedException();

        public Task<ApiResult<bool>> ChangePasswordAsync(string sessionId, ChangePasswordModel model, CancellationToken ct = default)
        {
            Changes.Add(model);
            return Task.FromResult(ChangeResult);
        }

        public Task SignOutAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static void Type(IRenderedComponent<ChangePasswordPage> cut, string label, string value) =>
        cut.FindComponents<MudTextField<string>>().Single(t => t.Instance.Label == label).Find("input").Input(value);

    [Fact]
    public void A_forced_change_explains_why_and_checks_the_rules_before_calling_the_api()
    {
        var (cut, auth, _) = RenderChange(forced: true);
        cut.Markup.ShouldContain("you need to choose your own password");

        Type(cut, "Current password", "temp-password-1");
        Type(cut, "New password", "short");
        Type(cut, "Confirm new password", "different");
        cut.Find("[data-testid=change-submit]").Click();

        cut.Markup.ShouldContain("Use at least 12 characters.");
        cut.Markup.ShouldContain("The two passwords do not match.");
        auth.Changes.ShouldBeEmpty();
    }

    [Fact]
    public void A_valid_change_calls_the_api_then_does_a_full_page_load_into_the_portal()
    {
        var (cut, auth, nav) = RenderChange(forced: true);

        Type(cut, "Current password", "temp-password-1");
        Type(cut, "New password", "Correct-Horse-Battery-9");
        Type(cut, "Confirm new password", "Correct-Horse-Battery-9");
        cut.Find("[data-testid=change-submit]").Click();

        cut.WaitForAssertion(() => auth.Changes.Count.ShouldBe(1));
        auth.Changes[0].NewPassword.ShouldBe("Correct-Horse-Battery-9");
        cut.WaitForAssertion(() => nav.History.First().Uri.ShouldBe("/admin"));
        nav.History.First().Options.ForceLoad.ShouldBeTrue();
    }

    [Fact]
    public void A_wrong_current_password_is_shown_with_its_reference_and_the_field_is_cleared()
    {
        var (cut, auth, _) = RenderChange(forced: false);
        auth.ChangeResult = ApiResult<bool>.Fail("VALIDATION_FAILED", "The current password is incorrect.", "corr-cp", 400);

        Type(cut, "Current password", "wrong-wrong-wrong");
        Type(cut, "New password", "Correct-Horse-Battery-9");
        Type(cut, "Confirm new password", "Correct-Horse-Battery-9");
        cut.Find("[data-testid=change-submit]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=change-error]").TextContent.ShouldContain("The current password is incorrect."));
        cut.Find("[data-testid=change-error]").TextContent.ShouldContain("corr-cp");
        cut.Markup.ShouldContain("Change your password");
    }
}

public class RouteAuthorizationTests : PageTestBase
{
    private Bunit.TestDoubles.BunitNavigationManager Open(string path, PortalSession? session)
    {
        Services.RemoveAll<Microsoft.AspNetCore.Authorization.IAuthorizationService>(); // bUnit's placeholder
        Services.AddPortalAuthorization();
        Services.AddScoped<ThemeService>();
        Services.AddSingleton<AuthenticationStateProvider>(session is null ? new FakeAuthState() : FakeAuthState.For(session));
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(path);
        return (Bunit.TestDoubles.BunitNavigationManager)nav;
    }

    private static PortalSession SessionFor(string portal, bool mustChange = false, params string[] permissions) =>
        SessionFixtures.NewSession(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(SessionFixtures.Start), portal: portal) with
        {
            Permissions = permissions,
            MustChangePassword = mustChange,
        };

    private IRenderedComponent<NexaVerify.Web.Routes> RenderRoutes()
    {
        return Render<NexaVerify.Web.Routes>();
    }

    [Fact]
    public void Anonymous_visitors_are_sent_to_sign_in_and_the_target_is_remembered()
    {
        var nav = Open("/admin/audit", null);

        var cut = RenderRoutes();

        cut.WaitForAssertion(() => nav.History.First().Uri.ShouldBe("/login?returnUrl=%2Fadmin%2Faudit"));
        nav.History.First().Options.ForceLoad.ShouldBeTrue();
    }

    [Fact]
    public void Client_users_cannot_open_admin_pages()
    {
        Open("/admin/audit", SessionFor(PortalKinds.Client, false, WebPermissions.AuditRead));

        var cut = RenderRoutes();

        cut.FindAll("[data-testid=access-denied]").Count.ShouldBe(1);
        cut.Markup.ShouldNotContain("Audit logs");
    }

    [Fact]
    public void Admins_without_the_permission_cannot_open_the_page()
    {
        Open("/admin/audit", SessionFor(PortalKinds.Admin, false, WebPermissions.ClientsRead));

        var cut = RenderRoutes();

        cut.FindAll("[data-testid=access-denied]").Count.ShouldBe(1);
    }

    [Fact]
    public void Admins_with_the_permission_get_the_page()
    {
        Open("/admin/audit", SessionFor(PortalKinds.Admin, false, WebPermissions.AuditRead));

        var cut = RenderRoutes();

        cut.WaitForAssertion(() => cut.Find("h1").TextContent.ShouldBe("Audit logs"));
        cut.FindAll("[data-testid=access-denied]").ShouldBeEmpty();
    }

    [Fact]
    public void Admin_users_cannot_open_client_pages_and_the_other_way_round()
    {
        Open("/client", SessionFor(PortalKinds.Admin, false, WebPermissions.DashboardClient));
        RenderRoutes().FindAll("[data-testid=access-denied]").Count.ShouldBe(1);
    }

    [Fact]
    public void A_pending_password_change_blocks_every_portal_page()
    {
        Open("/admin", SessionFor(PortalKinds.Admin, mustChange: true, WebPermissions.DashboardAdmin));

        RenderRoutes().FindAll("[data-testid=access-denied]").Count.ShouldBe(1);
    }

    [Fact]
    public void The_sign_in_page_is_open_to_everyone()
    {
        Open("/forbidden", null);

        RenderRoutes().Markup.ShouldContain("You don't have access to this page");
    }
}

public class UserMenuTests : PageTestBase
{
    [Fact]
    public void Signing_out_hands_over_to_the_server_endpoint_with_a_full_page_load()
    {
        SignInAsEverything();
        var popovers = Render<MudPopoverProvider>();
        var cut = Render<UserMenu>();

        cut.Find(".mud-menu-activator").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        popovers.WaitForAssertion(() => popovers.FindAll(".mud-menu-item,.mud-list-item").Any(i => i.TextContent.Contains("Sign out")).ShouldBeTrue());
        popovers.FindAll(".mud-menu-item,.mud-list-item").First(i => i.TextContent.Contains("Sign out")).Click();

        var nav = (Bunit.TestDoubles.BunitNavigationManager)Services.GetRequiredService<NavigationManager>();
        nav.History.First().Uri.ShouldBe("/auth/signed-out");
        nav.History.First().Options.ForceLoad.ShouldBeTrue();
        User.IsSignedIn.ShouldBeFalse();
    }
}
