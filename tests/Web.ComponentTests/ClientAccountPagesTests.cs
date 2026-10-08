using System.Reflection;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Web.Security;
using ActivityPage = NexaVerify.Web.Pages.Client.Activity;
using ApiDocsPage = NexaVerify.Web.Pages.Client.ApiDocs;
using NotificationsPage = NexaVerify.Web.Pages.Client.Notifications;
using SettingsPage = NexaVerify.Web.Pages.Client.Settings;
using UsersPage = NexaVerify.Web.Pages.Client.Users;

namespace NexaVerify.Web.ComponentTests;

public class NotificationsTests : ClientPageTestBase
{
    [Fact]
    public void The_page_marks_unread_items_and_keeps_message_text_inert()
    {
        SignInAsClientAdmin();
        var cut = Render<NotificationsPage>();

        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(2));
        cut.FindAll("table [data-testid=mark-read]").Count.ShouldBe(1, "only the unread one can be marked");
        cut.Markup.ShouldContain("&lt;b&gt;20%&lt;/b&gt; left");
        cut.FindAll("table tbody b").ShouldBeEmpty();

        cut.Find("[data-testid=mark-read]").Click();
        cut.WaitForAssertion(() => Notifications.MarkedRead.ShouldBe([Guid.Parse("00000000-0000-0000-0000-000000000001")]));
    }

    [Fact]
    public void Empty_and_failed_feeds_use_the_standard_states()
    {
        SignInAsClientAdmin();
        Notifications.Override = q => Ok.Of(new NexaVerify.Contracts.Dashboards.NotificationFeedDto(0, new PagedResult<NexaVerify.Contracts.Dashboards.NotificationDto>([], 1, 25, 0)));
        var empty = Render<NotificationsPage>();
        empty.WaitForAssertion(() => empty.FindAll("[data-testid=empty-state]").Count.ShouldBe(1));

        Notifications.Override = _ => Ok.Fail<NexaVerify.Contracts.Dashboards.NotificationFeedDto>(correlation: "corr-n");
        var failed = Render<NotificationsPage>();
        failed.WaitForAssertion(() => failed.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-n"));
    }

    [Fact]
    public async Task The_bell_shows_the_unread_count_and_polls_gently_until_it_is_disposed()
    {
        SignInAsClientAdmin();
        var cut = Render<NexaVerify.Web.Components.NotificationBell>();
        cut.WaitForAssertion(() => Notifications.Calls.ShouldBe(1));
        cut.Markup.ShouldContain("Notifications, 3 unread");

        Notifications.Unread = 7;
        Clock.Advance(TimeSpan.FromSeconds(30));
        Notifications.Calls.ShouldBe(1, "nothing before the minute is up");
        Clock.Advance(TimeSpan.FromSeconds(30));
        cut.WaitForAssertion(() => Notifications.Calls.ShouldBe(2));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Notifications, 7 unread"));

        await ((IAsyncDisposable)cut.Instance).DisposeAsync();
        Clock.Advance(TimeSpan.FromMinutes(5));
        Thread.Sleep(100);
        Notifications.Calls.ShouldBe(2, "polling stops with the component");
    }

    [Fact]
    public void A_failed_poll_keeps_what_the_user_already_sees()
    {
        SignInAsClientAdmin();
        var cut = Render<NexaVerify.Web.Components.NotificationBell>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Notifications, 3 unread"));
        Notifications.Override = _ => Ok.Fail<NexaVerify.Contracts.Dashboards.NotificationFeedDto>();

        Clock.Advance(TimeSpan.FromSeconds(60));

        cut.WaitForAssertion(() => Notifications.Calls.ShouldBe(2));
        cut.Markup.ShouldContain("Notifications, 3 unread");
    }

    [Fact]
    public void Users_without_the_permission_get_no_bell_and_no_calls()
    {
        SignInAs(WebPermissions.DashboardClient);

        var cut = Render<NexaVerify.Web.Components.NotificationBell>();

        cut.FindAll("[data-testid=notifications]").ShouldBeEmpty();
        Notifications.Calls.ShouldBe(0);
    }

    [Fact]
    public void Marking_one_as_read_updates_the_shared_badge()
    {
        SignInAsClientAdmin();
        var bell = Render<NexaVerify.Web.Components.NotificationBell>();
        bell.WaitForAssertion(() => bell.Markup.ShouldContain("Notifications, 3 unread"));
        var page = Render<NotificationsPage>();
        page.WaitForAssertion(() => page.FindAll("table [data-testid=mark-read]").Count.ShouldBe(1));

        page.Find("[data-testid=mark-read]").Click();

        bell.WaitForAssertion(() => bell.Markup.ShouldContain("Notifications, 2 unread"));
    }
}

public class UsersAndSettingsPagesTests : ClientPageTestBase
{
    [Fact]
    public void People_are_listed_with_plain_role_names_and_names_stay_text()
    {
        SignInAsClientAdmin();
        var cut = Render<UsersPage>();

        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));
        cut.Find("table tbody tr").TextContent.ShouldContain("Team member");
        cut.Markup.ShouldContain("Una &lt;i&gt;User&lt;/i&gt;");
        cut.FindAll("table tbody i").ShouldBeEmpty();
    }

    [Fact]
    public void Inviting_someone_calls_the_api_with_the_chosen_role()
    {
        SignInAsClientAdmin();
        var providers = Providers();
        var cut = Render<UsersPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=invite-user]").Count.ShouldBe(1));

        cut.Find("[data-testid=invite-user]").Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=client-user-fields]").Count.ShouldBe(1));
        providers.Find("[data-testid=form-submit]").Click();
        providers.WaitForAssertion(() => providers.Markup.ShouldContain("email address"));
        Account.Calls.ShouldBeEmpty();

        Fill(providers, "Email address", "new@acme.test");
        Fill(providers, "Full name", "Nia New");
        providers.Find("[data-testid=form-submit]").Click();

        cut.WaitForAssertion(() => Account.Calls.ShouldContain("invite:new@acme.test:ClientUser"));
    }

    [Fact]
    public void Turning_off_access_asks_first_then_disables_the_account()
    {
        SignInAsClientAdmin();
        var providers = Providers();
        var cut = Render<UsersPage>();
        cut.WaitForAssertion(() => cut.FindAll("table [data-testid=toggle-user]").Count.ShouldBe(1));

        cut.Find("[data-testid=toggle-user]").Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=confirm-ok]").Count.ShouldBe(1));
        Account.Calls.ShouldBeEmpty();
        providers.Find("[data-testid=confirm-ok]").Click();

        cut.WaitForAssertion(() => Account.Calls.ShouldContain("update-user:ClientUser:False"));
    }

    [Fact]
    public void The_owner_cannot_have_their_access_turned_off_from_the_list()
    {
        SignInAsClientAdmin();
        Account.Users = () => Task.FromResult(Ok.Page(ClientSample.User() with { IsOwner = true, Role = "ClientAdmin" }));

        var cut = Render<UsersPage>();

        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));
        cut.FindAll("table [data-testid=toggle-user]").ShouldBeEmpty();
    }

    [Fact]
    public void Settings_show_groups_and_read_only_limits_and_save_through_the_right_area()
    {
        SignInAsClientAdmin();
        var cut = Render<SettingsPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=settings-Connections]").Count.ShouldBe(1));

        var limits = cut.Find("[data-testid='settings-Limits of your plan']");
        limits.TextContent.ShouldContain("60");
        limits.TextContent.ShouldContain("set by NexaVerify");
        limits.QuerySelectorAll("button").ShouldBeEmpty();

        cut.Find("[data-testid=settings-Connections] [data-testid=settings-edit]").Click();
        Fill(cut, "IP addresses allowed to use the API keys.", "203.0.113.7\n198.51.100.0/24");
        cut.Find("[data-testid=settings-Connections] [data-testid=settings-save]").Click();

        cut.WaitForAssertion(() => Account.Calls.ShouldContain("settings:security:integration.allowedIps=[\"203.0.113.7\",\"198.51.100.0/24\"]"));
    }

    [Fact]
    public void Without_the_permission_a_group_is_read_only()
    {
        SignInAs(WebPermissions.ClientProfileRead);
        var cut = Render<SettingsPage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=settings-Notifications]").Count.ShouldBe(1));
        cut.FindAll("[data-testid=settings-edit]").ShouldBeEmpty();
        cut.FindAll("[data-testid=edit-company]").ShouldBeEmpty();
    }

    [Fact]
    public void Company_details_can_be_edited_by_people_who_may()
    {
        SignInAsClientAdmin();
        var providers = Providers();
        var cut = Render<SettingsPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=edit-company]").Count.ShouldBe(1));

        cut.Find("[data-testid=edit-company]").Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=company-fields]").Count.ShouldBe(1));
        providers.Find("[data-testid=form-submit]").Click();

        cut.WaitForAssertion(() => Account.Calls.ShouldContain("profile:Acme Corp"));
    }

    [Fact]
    public void The_account_card_links_to_the_password_change_page()
    {
        SignInAsClientAdmin();
        var cut = Render<SettingsPage>();

        cut.Find("[data-testid=change-password-link]").GetAttribute("href").ShouldBe("change-password");
        cut.Find("[data-testid=account-card]").TextContent.ShouldContain("una@acme.test");
    }

    [Fact]
    public void Activity_shows_sign_ins_and_account_changes()
    {
        SignInAsClientAdmin();
        var cut = Render<ActivityPage>();

        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBeGreaterThan(0));
        cut.Markup.ShouldContain("una@acme.test");
    }
}

public class ApiDocsPageTests : ClientPageTestBase
{
    [Fact]
    public void The_guide_covers_the_integration_topics()
    {
        SignInAsClientAdmin();
        var cut = Render<ApiDocsPage>();

        foreach (var topic in new[] { "X-Api-Key", "Idempotency-Key", "Retry-After", "X-Signature", "recognition.completed", "/faces/enroll", "/faces/verify", "/faces/identify", "/faces/balance" })
        {
            cut.Markup.ShouldContain(topic);
        }

        cut.Markup.ShouldContain("t + \".\" + body");
    }

    [Fact]
    public void Every_error_code_of_the_contract_is_listed()
    {
        SignInAsClientAdmin();
        var cut = Render<ApiDocsPage>();
        var shown = cut.FindAll("[data-testid=error-codes] tbody td code").Select(c => c.TextContent).ToList();

        var contract = typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();

        contract.Count.ShouldBeGreaterThan(20);
        shown.ShouldBe(contract, ignoreOrder: true);
    }

    [Fact]
    public void Code_is_always_plain_text_in_pre_blocks_and_never_contains_a_real_key()
    {
        SignInAsClientAdmin();
        var cut = Render<ApiDocsPage>();

        cut.FindAll("pre").Count.ShouldBeGreaterThan(5);
        cut.FindAll("pre *:not(code)").ShouldBeEmpty("code samples have no child markup");
        cut.Markup.ShouldNotContain("nv_live_", Case.Sensitive);
        cut.Markup.ShouldNotContain("whsec_", Case.Sensitive);
        cut.Markup.ShouldContain("YOUR_API_KEY");
    }

    [Fact]
    public void Samples_can_be_copied()
    {
        SignInAsClientAdmin();
        var cut = Render<ApiDocsPage>();

        cut.FindAll("[data-testid=code-copy]").First().Click();

        Services.GetRequiredService<FakeClipboard>().Last.ShouldNotBeNullOrEmpty();
        Services.GetRequiredService<FakeClipboard>().Last!.ShouldContain("X-Api-Key");
    }

    [Fact]
    public void The_code_sample_component_never_renders_markup()
    {
        const string hostile = "<script>alert('x')</script><img src=x onerror=alert(1)>";

        var cut = Render<NexaVerify.Web.Components.CodeSample>(p => p.Add(x => x.Title, "Hostile").Add(x => x.Code, hostile));

        cut.FindAll("pre script, pre img").ShouldBeEmpty();
        cut.Find("pre").TextContent.ShouldBe(hostile);
        cut.Find("[data-testid=code-copy]").Click();
        Services.GetRequiredService<FakeClipboard>().Last.ShouldBe(hostile);
    }
}

public class ClientNavigationTests
{
    [Fact]
    public void Every_client_menu_entry_is_built_and_points_at_a_real_page()
    {
        var routes = typeof(NexaVerify.Web.Routes).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>())
            .Select(r => r.Template.Trim('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = NavigationCatalog.Client.SelectMany(g => g.Items).ToList();

        items.ShouldAllBe(i => i.Implemented, "no client entry may still say 'soon'");
        foreach (var item in items)
        {
            routes.ShouldContain(item.Href.Trim('/'), $"'{item.Title}' links to {item.Href}");
        }
    }

    [Fact]
    public void A_client_user_only_sees_what_their_permissions_allow()
    {
        var visible = NavigationCatalog.Trim(NavigationCatalog.Client, p => SystemRoles.ClientUserPermissions.Contains(p)).SelectMany(g => g.Items).Select(i => i.Href).ToList();

        visible.ShouldContain("client/verify");
        visible.ShouldContain("client/history");
        visible.ShouldContain("client/api-docs");
        visible.ShouldNotContain("client/api-keys");
        visible.ShouldNotContain("client/webhooks");
        visible.ShouldNotContain("client/users");
        visible.ShouldNotContain("client/enroll");
    }
}

public class ClientRouteAuthorizationTests : PageTestBase
{
    public static TheoryData<string, string> Guarded => new()
    {
        { "/client/license", WebPermissions.LicenseRead },
        { "/client/enroll", WebPermissions.FacesEnroll },
        { "/client/verify", WebPermissions.FacesVerify },
        { "/client/identify", WebPermissions.FacesIdentify },
        { "/client/profiles", WebPermissions.FacesRead },
        { "/client/history", WebPermissions.FacesHistory },
        { "/client/api-keys", WebPermissions.ApiKeysRead },
        { "/client/api-logs", WebPermissions.ApiKeysRead },
        { "/client/webhooks", WebPermissions.WebhooksManage },
        { "/client/notifications", WebPermissions.NotificationsRead },
        { "/client/users", WebPermissions.UsersManage },
        { "/client/settings", WebPermissions.ClientProfileRead },
        { "/client/activity", WebPermissions.AuditReadClient },
    };

    private void Open(string path, string portal, params string[] permissions)
    {
        Services.RemoveAll<Microsoft.AspNetCore.Authorization.IAuthorizationService>();
        Services.AddPortalAuthorization();
        Services.AddScoped<ThemeService>();
        var session = SessionFixtures.NewSession(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(SessionFixtures.Start), portal: portal) with { Permissions = permissions };
        Services.AddSingleton<AuthenticationStateProvider>(FakeAuthState.For(session));
        Services.GetRequiredService<NavigationManager>().NavigateTo(path);
    }

    [Theory]
    [MemberData(nameof(Guarded))]
    public void A_page_needs_its_permission(string path, string permission)
    {
        Open(path, PortalKinds.Client, WebPermissions.ClientAdminDefaults.Where(p => p != permission).ToArray());

        Render<NexaVerify.Web.Routes>().FindAll("[data-testid=access-denied]").Count.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(Guarded))]
    public void A_page_opens_with_its_permission(string path, string permission)
    {
        Open(path, PortalKinds.Client, permission);

        var cut = Render<NexaVerify.Web.Routes>();

        cut.WaitForAssertion(() => cut.FindAll("h1").Count.ShouldBeGreaterThan(0));
        cut.FindAll("[data-testid=access-denied]").ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Guarded))]
    public void Platform_staff_cannot_open_client_pages(string path, string permission)
    {
        Open(path, PortalKinds.Admin, permission);

        Render<NexaVerify.Web.Routes>().FindAll("[data-testid=access-denied]").Count.ShouldBe(1);
    }

    [Fact]
    public void The_api_guide_is_open_to_every_client_user()
    {
        Open("/client/api-docs", PortalKinds.Client);

        var cut = Render<NexaVerify.Web.Routes>();

        cut.WaitForAssertion(() => cut.Find("h1").TextContent.ShouldBe("API guide"));
    }
}
