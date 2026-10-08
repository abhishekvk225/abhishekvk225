using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Tenancy;
using ClientDetailPage = NexaVerify.Web.Pages.Admin.ClientDetail;
using ClientsPage = NexaVerify.Web.Pages.Admin.Clients;
using CreateClientPage = NexaVerify.Web.Pages.Admin.CreateClient;

namespace NexaVerify.Web.ComponentTests;

public class ClientPagesTests : PageTestBase
{
    private static ApiResult<PagedResult<ClientListItemDto>> PageOf(params ClientListItemDto[] items) =>
        ApiResult<PagedResult<ClientListItemDto>>.Ok(new PagedResult<ClientListItemDto>(items, 1, 25, items.Length));

    [Fact]
    public void The_list_shows_a_skeleton_while_loading()
    {
        SignInAsEverything();
        var gate = new Gate<ApiResult<PagedResult<ClientListItemDto>>>();
        Clients.List = (_, _) => gate.Task;
        Providers();

        var cut = Render<ClientsPage>();

        cut.FindAll("[data-testid=skeleton-table]").Count.ShouldBe(1);
        gate.Release(PageOf());
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=skeleton-table]").ShouldBeEmpty());
    }

    [Fact]
    public void The_list_shows_rows_with_links_and_status_chips()
    {
        SignInAsEverything();
        var acme = Sample.ClientItem("Acme Corp");
        Clients.List = (_, _) => Task.FromResult(PageOf(acme, Sample.ClientItem("Globex", "Suspended")));
        Providers();

        var cut = Render<ClientsPage>();

        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(2));
        cut.Find("table tbody tr a").GetAttribute("href").ShouldBe($"admin/clients/{acme.Id}");
        cut.FindAll("table tbody tr .nv-status-chip").Select(c => c.GetAttribute("data-status")).ShouldBe(["Active", "Suspended"]);
    }

    [Fact]
    public void An_empty_list_invites_the_first_client()
    {
        SignInAsEverything();
        Providers();

        var cut = Render<ClientsPage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=empty-state]").Count.ShouldBe(1));
        cut.Markup.ShouldContain("Create the first one");
    }

    [Fact]
    public void A_failed_load_shows_the_reference_and_retry_works()
    {
        SignInAsEverything();
        var attempts = 0;
        Clients.List = (_, _) => Task.FromResult(++attempts == 1
            ? ApiResult<PagedResult<ClientListItemDto>>.Fail("INTERNAL_ERROR", "Something went wrong on our side. Please try again.", "corr-55", 500)
            : PageOf(Sample.ClientItem()));
        Providers();

        var cut = Render<ClientsPage>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-55"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Try again")).Click();
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));
    }

    [Fact]
    public void Creating_clients_is_only_offered_to_users_who_may_do_it()
    {
        SignInAs(WebPermissions.ClientsRead);
        Providers();
        Render<ClientsPage>().FindAll("[data-testid=new-client]").ShouldBeEmpty();

        SignInAs(WebPermissions.ClientsRead, WebPermissions.ClientsCreate);
        Render<ClientsPage>().FindAll("[data-testid=new-client]").Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_status_filter_is_sent_to_the_api()
    {
        SignInAsEverything();
        Providers();
        var cut = Render<ClientsPage>();
        cut.WaitForAssertion(() => Clients.Calls.Count.ShouldBeGreaterThan(0));

        var select = cut.FindComponent<MudSelect<string>>();
        await cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync("Suspended"));

        cut.WaitForAssertion(() => Clients.Calls.ShouldContain(c => c.EndsWith(":Suspended:1", StringComparison.Ordinal)));
    }

    private IRenderedComponent<ClientDetailPage> RenderDetail(Guid? id = null)
    {
        var client = Sample.Client(id ?? Guid.NewGuid());
        Clients.Get = _ => Task.FromResult(ApiResult<NexaVerify.Contracts.Tenancy.ClientDto>.Ok(client));
        return Render<ClientDetailPage>(p => p.Add(x => x.Id, client.Id));
    }

    [Fact]
    public void The_detail_page_has_the_tabs_the_user_may_see()
    {
        SignInAs(WebPermissions.ClientsRead);
        Providers();

        var cut = RenderDetail();

        cut.WaitForAssertion(() => cut.FindAll("[role=tab]").Select(t => t.TextContent.Trim()).ShouldBe(["Overview", "Users"]));
        cut.Markup.ShouldContain("Acme Corporation Ltd");
        cut.Markup.ShouldNotContain("data-testid=\"suspend-client\"");
    }

    [Fact]
    public void Everything_is_offered_to_a_super_admin()
    {
        SignInAsEverything();
        Providers();

        var cut = RenderDetail();

        cut.WaitForAssertion(() => cut.FindAll("[role=tab]").Select(t => t.TextContent.Trim()).ShouldBe(["Overview", "Users", "Settings", "License", "Audit"]));
        cut.FindAll("[data-testid=suspend-client]").Count.ShouldBe(1);
        cut.FindAll("[data-testid=edit-client]").Count.ShouldBe(1);
    }

    [Fact]
    public void A_missing_client_shows_a_friendly_error_with_retry()
    {
        SignInAsEverything();
        Providers();
        Clients.Get = _ => Task.FromResult(ApiResult<NexaVerify.Contracts.Tenancy.ClientDto>.Fail("NOT_FOUND", "We couldn't find that. It may have been removed.", "corr-1", 404));

        var cut = Render<ClientDetailPage>(p => p.Add(x => x.Id, Guid.NewGuid()));

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("We couldn't find that"));
        cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-1");
    }

    [Fact]
    public void Suspending_asks_for_a_reason_then_calls_the_api_and_confirms()
    {
        SignInAsEverything();
        var providers = Providers();
        var cut = RenderDetail();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=suspend-client]").Count.ShouldBe(1));

        cut.Find("[data-testid=suspend-client]").Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=confirm-ok]").Count.ShouldBe(1));
        providers.Find("[data-testid=confirm-ok]").HasAttribute("disabled").ShouldBeTrue("a reason is required first");

        providers.Find(".mud-dialog textarea").Input("Payment overdue");
        providers.Find("[data-testid=confirm-ok]").Click();

        cut.WaitForAssertion(() => Clients.Calls.ShouldContain("suspend:Payment overdue"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=activate-client]").Count.ShouldBe(1));
        cut.FindAll("[data-testid=suspend-client]").ShouldBeEmpty();
    }

    [Fact]
    public void Cancelling_the_confirmation_changes_nothing()
    {
        SignInAsEverything();
        var providers = Providers();
        var cut = RenderDetail();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=suspend-client]").Count.ShouldBe(1));

        cut.Find("[data-testid=suspend-client]").Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=confirm-cancel]").Count.ShouldBe(1));
        providers.Find("[data-testid=confirm-cancel]").Click();

        Clients.Calls.ShouldNotContain(c => c.StartsWith("suspend", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failed_suspension_is_reported_with_its_reference_and_the_page_stays_as_it_was()
    {
        SignInAsEverything();
        var providers = Providers();
        var snacks = Render<MudSnackbarProvider>();
        Clients.Suspend = (_, _) => Task.FromResult(ApiResult<ClientDto>.Fail("CLIENT_SUSPENDED", "Something went wrong on our side. Please try again.", "corr-909", 500));
        var cut = RenderDetail();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=suspend-client]").Count.ShouldBe(1));

        cut.Find("[data-testid=suspend-client]").Click();
        providers.WaitForAssertion(() => providers.FindAll(".mud-dialog textarea").Count.ShouldBe(1));
        providers.Find(".mud-dialog textarea").Input("because");
        providers.Find("[data-testid=confirm-ok]").Click();

        snacks.WaitForAssertion(() => snacks.Markup.ShouldContain("corr-909"));
        cut.FindAll("[data-testid=suspend-client]").Count.ShouldBe(1);
    }

    [Fact]
    public void Wizard_blocks_each_step_until_its_fields_are_valid()
    {
        SignInAsEverything();
        Providers();
        var cut = Render<CreateClientPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=wizard-next]").Count.ShouldBe(1));

        cut.Find("[data-testid=wizard-next]").Click();

        cut.Markup.ShouldContain("Enter a short code for the client.");
        cut.Markup.ShouldContain("Enter the company name.");
        cut.Find("[data-testid=wizard-steps] .nv-step-current").TextContent.ShouldContain("Company");
        cut.FindAll("[data-testid=wizard-back]").Single().HasAttribute("disabled").ShouldBeTrue();
    }

    [Fact]
    public void Wizard_walks_through_all_steps_and_creates_the_client()
    {
        SignInAsEverything();
        Providers();
        var cut = Render<CreateClientPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=wizard-next]").Count.ShouldBe(1));

        Fill(cut, "Client code", "NEWCO");
        Fill(cut, "Company name", "New Co");
        Fill(cut, "Contact email", "ops@newco.test");
        cut.Find("[data-testid=wizard-next]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=wizard-steps] .nv-step-current").TextContent.ShouldContain("Administrator"));
        Fill(cut, "Administrator's full name", "Nia Admin");
        Fill(cut, "Administrator's email", "nia@newco.test");
        cut.Find("[data-testid=wizard-next]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=wizard-steps] .nv-step-current").TextContent.ShouldContain("First license"));
        cut.Find("[data-testid=wizard-next]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=wizard-steps] .nv-step-current").TextContent.ShouldContain("Review"));
        cut.Markup.ShouldContain("New Co");
        cut.Markup.ShouldContain("nia@newco.test");
        cut.Find("[data-testid=wizard-next]").TextContent.ShouldContain("Create client");
        cut.Find("[data-testid=wizard-next]").Click();

        cut.WaitForAssertion(() => Clients.Calls.ShouldContain("create:NEWCO"));
    }

    [Fact]
    public void Server_side_field_errors_send_the_user_back_to_the_step_that_holds_the_field()
    {
        SignInAsEverything();
        Providers();
        Clients.Create = _ => Task.FromResult(ApiResult<ClientDto>.Fail(new ApiError("DUPLICATE_EXTERNAL_REF", "That code is already used.", "corr-3", 409,
            new Dictionary<string, string[]> { ["code"] = ["A client with this code already exists."] })));
        var cut = Render<CreateClientPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=wizard-next]").Count.ShouldBe(1));
        Fill(cut, "Client code", "DUP");
        Fill(cut, "Company name", "Dup Co");
        Fill(cut, "Contact email", "a@dup.test");
        cut.Find("[data-testid=wizard-next]").Click();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Administrator's email"));
        Fill(cut, "Administrator's full name", "Dee");
        Fill(cut, "Administrator's email", "dee@dup.test");
        cut.Find("[data-testid=wizard-next]").Click();
        cut.Find("[data-testid=wizard-next]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=wizard-next]").TextContent.ShouldContain("Create client"));

        cut.Find("[data-testid=wizard-next]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=wizard-error]").TextContent.ShouldContain("corr-3"));
        cut.Find("[data-testid=wizard-steps] .nv-step-current").TextContent.ShouldContain("Company");
        cut.Markup.ShouldContain("A client with this code already exists.");
    }

    private static void Fill(IRenderedComponent<IComponent> cut, string label, string value)
    {
        var field = cut.FindComponents<MudTextField<string>>().Single(t => t.Instance.Label == label);
        field.Find("input").Input(value);
    }
}
