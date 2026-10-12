using Bunit.TestDoubles;
using NexaVerify.Contracts.Common;
using BankPage = NexaVerify.Web.Pages.Client.Verification.BankStatementVerification;
using DetailsPage = NexaVerify.Web.Pages.Client.Verification.VerificationDetails;
using HistoryPage = NexaVerify.Web.Pages.Client.Verification.VerificationHistory;
using OverviewPage = NexaVerify.Web.Pages.Client.Verification.VerificationOverview;
using PanPage = NexaVerify.Web.Pages.Client.Verification.PanVerification;
using PassportPage = NexaVerify.Web.Pages.Client.Verification.PassportVerification;

namespace NexaVerify.Web.ComponentTests;

public class VerificationOverviewTests : VerificationPageTestBase
{
    [Fact]
    public void Shows_a_skeleton_then_available_modules_and_muted_upcoming_tiles_without_any_action()
    {
        SignInAsVerifier();
        var gate = new Gate<ApiResult<IReadOnlyList<VerificationModuleInfo>>>();
        Api.Modules = () => gate.Task;

        var cut = Render<OverviewPage>();
        cut.FindAll("[data-testid=overview-loading]").Count.ShouldBe(1);
        gate.Release(ApiResult<IReadOnlyList<VerificationModuleInfo>>.Ok(VerificationSample.Modules()));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=module-card]").Count.ShouldBe(3));
        var upcoming = cut.FindAll("[data-testid=upcoming-card]");
        upcoming.Count.ShouldBe(6);
        foreach (var tile in upcoming)
        {
            tile.QuerySelectorAll("a, button, input, [tabindex='0']").Where(e => e.GetAttribute("tabindex") != "-1").ShouldBeEmpty("an upcoming tile offers nothing to click");
            tile.GetAttribute("aria-disabled").ShouldBe("true");
            tile.TextContent.ShouldContain("Upcoming");
            tile.TextContent.ShouldContain("No credits are used");
            tile.TextContent.ShouldNotContain("credit cost", Case.Insensitive);
        }

        cut.Find("[data-testid=upcoming-modules]").QuerySelectorAll("a").ShouldBeEmpty();
        string.Join(' ', upcoming.Select(t => t.TextContent)).ShouldContain("Aadhaar");
    }

    [Fact]
    public void Start_buttons_follow_the_submit_permission_of_each_module()
    {
        SignInAsVerifier(WebPermissions.VerificationPassportSubmit);

        var cut = Render<OverviewPage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=module-card]").Count.ShouldBe(3));
        var links = cut.FindAll("[data-testid=module-start]").Select(a => a.GetAttribute("href")).ToList();
        links.ShouldBe(["client/verification/bank-statement", "client/verification/pan"], ignoreOrder: true);
    }

    [Fact]
    public void A_disabled_module_says_temporarily_unavailable_and_has_no_start_button()
    {
        SignInAsVerifier();
        Api.Modules = () => Ok.Of(VerificationSample.Modules(disabled: ["pan"]));

        var cut = Render<OverviewPage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=module-card]").Count.ShouldBe(3));
        var pan = cut.FindAll("[data-testid=module-card]").Single(c => c.GetAttribute("data-module") == "pan");
        pan.TextContent.ShouldContain("Temporarily unavailable");
        pan.QuerySelectorAll("[data-testid=module-start]").ShouldBeEmpty();
    }

    [Fact]
    public void An_error_shows_the_reference_and_retry_reloads()
    {
        SignInAsVerifier();
        var attempts = 0;
        Api.Modules = () => ++attempts == 1 ? Ok.Fail<IReadOnlyList<VerificationModuleInfo>>(correlation: "corr-mod") : Ok.Of(VerificationSample.Modules());

        var cut = Render<OverviewPage>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-mod"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Try again")).Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=module-card]").Count.ShouldBe(3));
    }

    [Fact]
    public void An_empty_catalogue_is_an_empty_state()
    {
        SignInAsVerifier();
        Api.Modules = () => Ok.Of<IReadOnlyList<VerificationModuleInfo>>([]);

        var cut = Render<OverviewPage>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=empty-state]").TextContent.ShouldContain("No document checks"));
    }

    [Fact]
    public void The_page_has_one_h1_and_states_honestly_what_checks_do()
    {
        SignInAsVerifier();
        var cut = Render<OverviewPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=module-card]").Count.ShouldBe(3));

        cut.FindAll("h1").Count.ShouldBe(1);
        cut.Find("[data-testid=overview-note]").TextContent.ShouldContain("cannot show that a document is real");
    }
}

public class VerificationSubmitTests : VerificationPageTestBase
{
    private IRenderedComponent<BankPage> RenderBank(IRenderedComponent<MudDialogProvider>? providers = null)
    {
        var cut = Render<BankPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=document-upload]").Count.ShouldBe(1));
        return cut;
    }

    private static void Consent(IRenderedComponent<BankPage> cut, string text = "FORM-12") => Fill(cut, "Where consent is recorded", text);

    [Fact]
    public void The_page_explains_purpose_and_says_checks_do_not_prove_a_document_is_real()
    {
        SignInAsVerifier();
        var cut = RenderBank();

        cut.Find("[data-testid=purpose-note]").TextContent.ShouldContain("does not prove the document is real");
        cut.Find("[data-testid=upload-rules]").TextContent.ShouldContain("Up to 10 MB");
        cut.Find("[data-testid=upload-rules]").TextContent.ShouldContain("30 pages");
        cut.FindAll("h1").Count.ShouldBe(1);
    }

    [Fact]
    public void A_wrong_type_is_refused_before_anything_is_sent()
    {
        SignInAsVerifier();
        var cut = RenderBank();

        Upload(cut, [1, 2, 3, 4], "notes.txt", "text/plain");

        cut.WaitForAssertion(() => cut.Find("[data-testid=file-problem]").TextContent.ShouldContain("not accepted"));
        cut.FindAll("[data-testid=file-ready]").ShouldBeEmpty();
        Api.Calls.ShouldNotContain("submit-bank");
    }

    [Fact]
    public void An_empty_file_is_refused()
    {
        SignInAsVerifier();
        var cut = RenderBank();

        Upload(cut, [], "empty.pdf");

        cut.WaitForAssertion(() => cut.Find("[data-testid=file-problem]").TextContent.ShouldContain("empty"));
    }

    [Fact]
    public void An_oversized_file_is_refused_with_the_limit_from_the_catalogue()
    {
        SignInAsVerifier();
        Api.Modules = () => Ok.Of(VerificationSample.Modules(maxBytes: 1024));
        var cut = RenderBank();

        Upload(cut, Pdf(4096), "big.pdf");

        cut.WaitForAssertion(() => cut.Find("[data-testid=file-problem]").TextContent.ShouldContain("too large"));
        cut.Find("[data-testid=file-problem]").TextContent.ShouldContain("1 KB");
    }

    [Fact]
    public void A_renamed_file_is_caught_by_its_contents()
    {
        SignInAsVerifier();
        var cut = RenderBank();

        Upload(cut, Png(), "statement.pdf");

        cut.WaitForAssertion(() => cut.Find("[data-testid=file-problem]").TextContent.ShouldContain("contents are PNG"));
    }

    [Fact]
    public void A_file_that_is_none_of_the_accepted_kinds_is_refused_even_with_a_good_name()
    {
        SignInAsVerifier();
        var cut = RenderBank();

        Upload(cut, [0x4D, 0x5A, 0x90, 0x00, 1, 2], "statement.pdf");

        cut.WaitForAssertion(() => cut.Find("[data-testid=file-problem]").TextContent.ShouldContain("does not look like an accepted document"));
    }

    [Fact]
    public void A_pdf_with_too_many_pages_gets_a_warning_hint_but_can_still_be_sent()
    {
        SignInAsVerifier();
        Api.Modules = () => Ok.Of<IReadOnlyList<VerificationModuleInfo>>(
            [VerificationSample.Modules().First() with { Limits = VerificationSample.Limits(pages: 2) }]);
        var cut = RenderBank();
        var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n" + string.Concat(Enumerable.Repeat("<< /Type /Page >>\n", 3)));

        Upload(cut, pdf, "s.pdf");

        cut.WaitForAssertion(() => cut.Find("[data-testid=file-hint]").TextContent.ShouldContain("about 3 pages"));
        cut.FindAll("[data-testid=file-ready]").Count.ShouldBe(1);
    }

    [Fact]
    public void Without_a_consent_reference_the_form_shows_an_inline_message_and_sends_nothing()
    {
        SignInAsVerifier();
        var cut = RenderBank();
        Upload(cut, Pdf(), "s.pdf");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=file-ready]").Count.ShouldBe(1));

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Enter where"));
        Api.Calls.ShouldNotContain("submit-bank");
    }

    [Fact]
    public void Submitting_without_a_document_says_so()
    {
        SignInAsVerifier();
        var cut = RenderBank();
        Consent(cut);

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find("[data-testid=document-problem]").TextContent.ShouldContain("Add the bank statement"));
        Api.Calls.ShouldNotContain("submit-bank");
    }

    [Fact]
    public void A_cost_confirmation_comes_first_and_cancelling_sends_nothing()
    {
        SignInAsVerifier();
        var providers = Providers();
        var cut = RenderBank();
        Upload(cut, Pdf(), "s.pdf");
        Consent(cut);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=file-ready]").Count.ShouldBe(1));

        cut.Find("form").Submit();

        providers.WaitForAssertion(() => providers.Find("[data-testid=confirm-message]").TextContent.ShouldContain("about 5 credits"));
        providers.Find("[data-testid=confirm-message]").TextContent.ShouldContain("nothing is charged");
        providers.Find("[data-testid=confirm-cancel]").Click();
        Api.Calls.ShouldNotContain("submit-bank");
    }

    [Fact]
    public void A_confirmed_submit_sends_the_details_the_document_and_a_key_then_waits_and_opens_the_details()
    {
        SignInAsVerifier();
        var providers = Providers();
        var nav = (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();
        var polls = 0;
        Api.Status = n => { polls = n; return Ok.Of(VerificationSample.Status(n < 3 ? "Processing" : "Completed")); };
        var cut = RenderBank();
        Upload(cut, Pdf(), "s.pdf");
        Fill(cut, "Account holder name (optional)", "Priya Sharma");
        Consent(cut);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=file-ready]").Count.ShouldBe(1));
        cut.Find("form").Submit();
        ConfirmDialog(providers);

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=accepted-panel]").Count.ShouldBe(1));
        var request = (SubmitBankStatementRequest)Api.LastRequest!;
        request.ConsentReference.ShouldBe("FORM-12");
        request.AccountHolderName.ShouldBe("Priya Sharma");
        Api.Keys.Single().ShouldNotBeNullOrWhiteSpace();
        Api.LastDocument!.ContentType.ShouldBe("application/pdf");
        cut.Find("[data-testid=accepted-ref]").TextContent.ShouldBe(VerificationText.ShortId(VerificationSample.Id));

        // The wait follows the 202's hint (2 s), then backs off: 2 s, then 3 s.
        for (var i = 0; i < 12 && nav.Uri.EndsWith("client/verification/" + VerificationSample.Id, StringComparison.Ordinal) == false; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(3));
            cut.WaitForState(() => true, TimeSpan.FromMilliseconds(30));
        }

        nav.Uri.ShouldEndWith("client/verification/" + VerificationSample.Id);
        Api.StatusCalls.ShouldAllBe(c => c.Passive, "waiting must not keep the session alive");
        polls.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void The_document_is_wiped_from_memory_after_it_was_accepted()
    {
        SignInAsVerifier();
        var providers = Providers();
        Api.Status = _ => Ok.Of(VerificationSample.Status("Processing"));
        byte[]? held = null;
        var cut = RenderBank();
        Upload(cut, Pdf(), "s.pdf");
        Consent(cut);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=file-ready]").Count.ShouldBe(1));
        held = cut.FindComponent<DocumentUpload>().Instance.Value!.Data;
        cut.Find("form").Submit();
        ConfirmDialog(providers);

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=accepted-panel]").Count.ShouldBe(1));

        held.ShouldAllBe(b => b == 0, "the portal never keeps a document");
    }

    [Fact]
    public void A_temporary_failure_keeps_the_file_and_the_same_key_so_a_retry_cannot_be_charged_twice()
    {
        SignInAsVerifier();
        var providers = Providers();
        Api.Submit = n => n == 1 ? Ok.Fail<VerificationAcceptedDto>("API_TIMEOUT", "slow", "corr-t", 504) : Ok.Of(VerificationSample.Accepted());
        Api.Status = _ => Ok.Of(VerificationSample.Status("Processing"));
        var cut = RenderBank();
        Upload(cut, Pdf(), "s.pdf");
        Consent(cut);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=file-ready]").Count.ShouldBe(1));

        cut.Find("form").Submit();
        ConfirmDialog(providers);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=submit-error]").Count.ShouldBe(1));
        cut.FindAll("[data-testid=file-kept]").Count.ShouldBe(1);
        cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-t");

        cut.Find("form").Submit();
        ConfirmDialog(providers);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=accepted-panel]").Count.ShouldBe(1));

        Api.Keys.Count.ShouldBe(2);
        Api.Keys[1].ShouldBe(Api.Keys[0]);
    }

    [Fact]
    public void A_definitive_refusal_uses_a_fresh_key_next_time_and_explains_in_plain_words()
    {
        SignInAsVerifier();
        var providers = Providers();
        Api.Submit = n => n == 1 ? Ok.Fail<VerificationAcceptedDto>("DOCUMENT_ENCRYPTED", "x", "corr-e", 422) : Ok.Of(VerificationSample.Accepted());
        Api.Status = _ => Ok.Of(VerificationSample.Status("Processing"));
        var cut = RenderBank();
        Upload(cut, Pdf(), "s.pdf");
        Consent(cut);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=file-ready]").Count.ShouldBe(1));

        cut.Find("form").Submit();
        ConfirmDialog(providers);
        cut.WaitForAssertion(() => cut.Find("[data-testid=submit-error]").TextContent.ShouldContain("password protected"));
        cut.Find("form").Submit();
        ConfirmDialog(providers);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=accepted-panel]").Count.ShouldBe(1));

        Api.Keys[1].ShouldNotBe(Api.Keys[0]);
    }

    [Fact]
    public void A_module_switched_off_on_the_server_shows_the_unavailable_state()
    {
        SignInAsVerifier();
        var providers = Providers();
        Api.Submit = _ => Ok.Fail<VerificationAcceptedDto>("VERIFICATION_MODULE_DISABLED", "off", "corr-d", 404);
        var cut = RenderBank();
        Upload(cut, Pdf(), "s.pdf");
        Consent(cut);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=file-ready]").Count.ShouldBe(1));
        cut.Find("form").Submit();
        ConfirmDialog(providers);

        cut.WaitForAssertion(() => cut.Find("[data-testid=module-disabled]").TextContent.ShouldContain("Temporarily unavailable"));
        cut.FindAll("[data-testid=submit-check]").ShouldBeEmpty();
    }

    [Fact]
    public void A_module_that_is_disabled_in_the_catalogue_never_shows_the_form()
    {
        SignInAsVerifier();
        Api.Modules = () => Ok.Of(VerificationSample.Modules(disabled: ["bank-statement"]));

        var cut = Render<BankPage>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=module-disabled]").TextContent.ShouldContain("switched off"));
        cut.FindAll("[data-testid=document-upload]").ShouldBeEmpty();
    }

    [Fact]
    public void While_loading_a_skeleton_shows_and_a_catalogue_error_offers_retry()
    {
        SignInAsVerifier();
        var gate = new Gate<ApiResult<IReadOnlyList<VerificationModuleInfo>>>();
        Api.Modules = () => gate.Task;
        var cut = Render<BankPage>();
        cut.FindAll("[data-testid=submit-loading]").Count.ShouldBe(1);

        gate.Release(ApiResult<IReadOnlyList<VerificationModuleInfo>>.Fail("API_UNAVAILABLE", "down", "corr-u", 503));

        cut.WaitForAssertion(() => cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-u"));
    }

    [Fact]
    public void Pan_can_be_checked_from_the_number_alone()
    {
        SignInAsVerifier();
        var providers = Providers();
        Api.Status = _ => Ok.Of(VerificationSample.Status("Processing"));
        var cut = Render<PanPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=document-upload]").Count.ShouldBe(1));
        Fill(cut, "PAN number", "abcde1234f");
        Fill(cut, "Name of the holder", "Priya Sharma");
        Fill(cut, "Where consent is recorded", "FORM-1");

        cut.Find("form").Submit();
        ConfirmDialog(providers);

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=accepted-panel]").Count.ShouldBe(1));
        var request = (SubmitPanRequest)Api.LastRequest!;
        request.PanNumber.ShouldBe("ABCDE1234F");
        Api.LastDocument.ShouldBeNull();
    }

    [Fact]
    public void Pan_with_neither_a_card_nor_a_number_is_refused_inline()
    {
        SignInAsVerifier();
        var cut = Render<PanPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=document-upload]").Count.ShouldBe(1));
        Fill(cut, "Name of the holder", "Priya Sharma");
        Fill(cut, "Where consent is recorded", "FORM-1");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find("[data-testid=document-problem]").TextContent.ShouldContain("PAN card or enter the PAN number"));
        Api.Calls.ShouldNotContain("submit-pan");
    }

    [Fact]
    public void A_badly_shaped_pan_number_is_explained_inline()
    {
        SignInAsVerifier();
        var cut = Render<PanPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=document-upload]").Count.ShouldBe(1));
        Fill(cut, "PAN number", "12345");
        Fill(cut, "Name of the holder", "Priya Sharma");
        Fill(cut, "Where consent is recorded", "FORM-1");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("A PAN has 5 letters, 4 digits and 1 letter"));
        Api.Calls.ShouldNotContain("submit-pan");
    }

    [Fact]
    public void Passport_needs_the_photo_page()
    {
        SignInAsVerifier();
        var cut = Render<PassportPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=document-upload]").Count.ShouldBe(1));
        Fill(cut, "Where consent is recorded", "FORM-1");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find("[data-testid=document-problem]").TextContent.ShouldContain("passport photo page"));
    }
}

public class VerificationHistoryTests : VerificationPageTestBase
{
    private IRenderedComponent<HistoryPage> RenderReady()
    {
        var cut = Render<HistoryPage>();
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBeGreaterThan(0));
        return cut;
    }

    private static IReadOnlyList<string> MenuItems(IRenderedComponent<MudPopoverProvider> providers) =>
        providers.FindAll("[data-testid^=act-]").Select(e => e.GetAttribute("data-testid")!).ToList();

    private static void OpenMenu(IRenderedComponent<HistoryPage> cut, IRenderedComponent<MudPopoverProvider> providers)
    {
        cut.FindAll("table tbody tr button.mud-menu-icon-button-activator")[0].Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=act-view]").Count.ShouldBeGreaterThan(0));
    }

    [Fact]
    public void Shows_request_type_date_and_status_columns_and_one_h1()
    {
        SignInAsVerifier();
        Providers();

        var cut = RenderReady();

        var headers = cut.FindAll("table th").Select(h => h.TextContent.Trim()).ToList();
        headers.ShouldContain("Request");
        headers.ShouldContain("Type");
        headers.ShouldContain("Submitted");
        headers.ShouldContain("Status");
        cut.FindAll("h1").Count.ShouldBe(1);
        cut.Find("table tbody tr").TextContent.ShouldContain("Checks completed");
    }

    [Fact]
    public void The_row_menu_holds_view_report_and_erase_for_a_finished_check_but_no_cancel()
    {
        SignInAsVerifier();
        var popovers = Render<MudPopoverProvider>();
        var providers = Render<MudDialogProvider>();
        var cut = RenderReady();

        OpenMenu(cut, popovers);

        var items = MenuItems(popovers);
        items.ShouldContain("act-view");
        items.ShouldContain("act-report-html");
        items.ShouldContain("act-report-json");
        items.ShouldContain("act-erase");
        items.ShouldNotContain("act-cancel");
        items.ShouldNotContain("act-csv", "transactions export is for bank statements only");
        popovers.Find("[data-testid=act-report-html]").GetAttribute("href").ShouldBe($"bff/client/verification/{VerificationSample.Id}/report?format=html");
    }

    [Fact]
    public void A_bank_statement_row_offers_the_transactions_csv()
    {
        SignInAsVerifier();
        var popovers = Render<MudPopoverProvider>();
        var providers = Render<MudDialogProvider>();
        Api.List = _ => Task.FromResult(Ok.Page(VerificationSample.Item("BankStatement", external: "NotRequested")));
        var cut = RenderReady();

        OpenMenu(cut, popovers);

        MenuItems(popovers).ShouldContain("act-csv");
        popovers.Find("[data-testid=act-csv]").GetAttribute("href").ShouldEndWith("/transactions.csv");
    }

    [Fact]
    public void A_running_check_can_be_cancelled_but_not_erased()
    {
        SignInAsVerifier();
        var popovers = Render<MudPopoverProvider>();
        var providers = Render<MudDialogProvider>();
        Api.List = _ => Task.FromResult(Ok.Page(VerificationSample.Item(status: "Processing", external: "NotRequested")));
        var cut = RenderReady();

        OpenMenu(cut, popovers);

        MenuItems(popovers).ShouldContain("act-cancel");
        MenuItems(popovers).ShouldNotContain("act-erase");
        popovers.Find("[data-testid=act-cancel]").Click();
        ConfirmDialog(providers);
        cut.WaitForAssertion(() => Api.Calls.ShouldContain("cancel"));
    }

    [Fact]
    public void Retry_appears_only_when_the_provider_gave_no_answer()
    {
        SignInAsVerifier();
        var popovers = Render<MudPopoverProvider>();
        var providers = Render<MudDialogProvider>();
        Api.List = _ => Task.FromResult(Ok.Page(VerificationSample.Item(external: "ProviderError")));
        var cut = RenderReady();

        OpenMenu(cut, popovers);

        MenuItems(popovers).ShouldContain("act-retry");
    }

    [Fact]
    public void Not_configured_cannot_be_retried_from_the_menu()
    {
        SignInAsVerifier();
        var popovers = Render<MudPopoverProvider>();
        var providers = Render<MudDialogProvider>();
        var cut = RenderReady();

        OpenMenu(cut, popovers);

        MenuItems(popovers).ShouldNotContain("act-retry");
    }

    [Fact]
    public void Entries_follow_permissions()
    {
        SignInAsVerifier(WebPermissions.VerificationErase, WebPermissions.VerificationReportDownload, WebPermissions.VerificationPanSubmit);
        var popovers = Render<MudPopoverProvider>();
        var providers = Render<MudDialogProvider>();
        Api.List = _ => Task.FromResult(Ok.Page(VerificationSample.Item(status: "Processing", external: "NotRequested")));
        var cut = RenderReady();

        OpenMenu(cut, popovers);

        MenuItems(popovers).ShouldBe(["act-view"]);
    }

    [Fact]
    public void Erase_needs_the_typed_word_and_only_then_calls_the_api()
    {
        SignInAsVerifier();
        var popovers = Render<MudPopoverProvider>();
        var providers = Render<MudDialogProvider>();
        var cut = RenderReady();
        OpenMenu(cut, popovers);

        popovers.Find("[data-testid=act-erase]").Click();

        providers.WaitForAssertion(() => providers.FindAll("[data-testid=confirm-input]").Count.ShouldBe(1));
        providers.Find("[data-testid=confirm-ok]").HasAttribute("disabled").ShouldBeTrue();
        Api.Calls.ShouldNotContain("erase");
        providers.FindComponents<MudTextField<string>>().First(f => f.Instance.Label == "Type to confirm").Find("input").Input("ERASE");
        providers.Find("[data-testid=confirm-ok]").Click();
        cut.WaitForAssertion(() => Api.Calls.ShouldContain("erase"));
    }

    [Fact]
    public void A_failed_action_reports_the_reason()
    {
        SignInAsVerifier();
        var popovers = Render<MudPopoverProvider>();
        var providers = Render<MudDialogProvider>();
        Api.List = _ => Task.FromResult(Ok.Page(VerificationSample.Item(status: "Processing", external: "NotRequested")));
        Api.Action = () => Ok.Fail<bool>("VERIFICATION_NOT_CANCELLABLE", "This check has already finished.", "corr-c", 409);
        var cut = RenderReady();
        OpenMenu(cut, popovers);

        popovers.Find("[data-testid=act-cancel]").Click();
        ConfirmDialog(providers);

        cut.WaitForAssertion(() => Api.Calls.ShouldContain("cancel"));
    }

    [Fact]
    public void Filters_are_sent_to_the_api_and_can_be_cleared()
    {
        SignInAsVerifier();
        var cut = RenderReady();

        cut.InvokeAsync(() => cut.FindComponents<MudSelect<string>>().First(s => s.Instance.Label == "Type").Instance.ValueChanged.InvokeAsync("Passport"));
        cut.WaitForAssertion(() => Api.LastQuery!.Type.ShouldBe("Passport"));
        cut.InvokeAsync(() => cut.FindComponents<MudSelect<string>>().First(s => s.Instance.Label == "Status").Instance.ValueChanged.InvokeAsync("ReviewRequired"));
        cut.WaitForAssertion(() => Api.LastQuery!.Status.ShouldBe("ReviewRequired"));
        cut.FindComponent<MudCheckBox<bool>>().Find("input").Change(true);
        cut.WaitForAssertion(() => Api.LastQuery!.ReviewRequired.ShouldBe(true));

        cut.Find("[data-testid=filter-clear]").Click();
        cut.WaitForAssertion(() => (Api.LastQuery!.Type, Api.LastQuery.Status, Api.LastQuery.ReviewRequired).ShouldBe((null, null, null)));
    }

    [Fact]
    public void No_history_is_an_empty_state_and_an_error_offers_retry()
    {
        SignInAsVerifier();
        var attempts = 0;
        Api.List = _ => ++attempts == 1 ? Ok.Fail<PagedResult<VerificationListItemDto>>(correlation: "corr-l") : Task.FromResult(ApiResult<PagedResult<VerificationListItemDto>>.Ok(new PagedResult<VerificationListItemDto>([], 1, 10, 0)));

        var cut = Render<HistoryPage>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-l"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Try again")).Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=empty-state]").TextContent.ShouldContain("not run any document checks"));
    }
}

public class VerificationDetailsTests : VerificationPageTestBase
{
    private IRenderedComponent<DetailsPage> RenderDetails() =>
        Render<DetailsPage>(p => p.Add(x => x.Id, VerificationSample.Id));

    [Fact]
    public void Shows_a_skeleton_while_loading()
    {
        SignInAsVerifier();
        var gate = new Gate<ApiResult<VerificationResultDto>>();
        Api.Result = () => gate.Task;

        var cut = RenderDetails();

        cut.FindAll("[data-testid=skeleton-card], [data-testid=dashboard-skeleton]").Count.ShouldBeGreaterThan(0);
        gate.Release(ApiResult<VerificationResultDto>.Ok(VerificationSample.Result()));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=stage-stepper]").Count.ShouldBe(1));
    }

    [Fact]
    public void Staged_outcomes_are_separate_steps_with_their_own_chips_and_one_h1()
    {
        SignInAsVerifier();
        var cut = RenderDetails();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=stage-stepper]").Count.ShouldBe(1));
        var steps = cut.FindAll("[data-testid=stage-stepper] li").Select(l => l.GetAttribute("data-stage")).ToList();
        steps.ShouldBe(["Uploaded", "Extracted", "Rules checked", "Consistency checked", "Official verification"]);
        cut.FindAll("h1").Count.ShouldBe(1);
        cut.Find("[data-testid=headline]").TextContent.ShouldContain("Checks completed. Not officially verified.");
    }

    [Theory]
    [InlineData("ProviderNotConfigured", "Official verification is not available for your account")]
    [InlineData("NotVerified", "The provider could not confirm these details")]
    [InlineData("Inconclusive", "The provider's answer was inconclusive")]
    [InlineData("ProviderError", "Official verification could not be completed. You can retry.")]
    [InlineData("Timeout", "Official verification could not be completed. You can retry.")]
    [InlineData("NotRequested", "No official verification was requested")]
    [InlineData("Pending", "Waiting for the provider")]
    public void Every_external_state_is_explained_in_plain_language_and_never_called_verified(string state, string expected)
    {
        SignInAsVerifier();
        Api.Result = () => Ok.Of(VerificationSample.Result(external: state));

        var cut = RenderDetails();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=external-card]").Count.ShouldBe(1));
        cut.Find("[data-testid=external-card]").GetAttribute("data-state").ShouldBe(state);
        cut.Find("[data-testid=external-headline]").TextContent.ShouldContain(expected);
        cut.Find("[data-testid=external-explanation]").TextContent.Length.ShouldBeGreaterThan(30);
        cut.Find("[data-testid=headline]").TextContent.ShouldNotContain("Officially verified by");
    }

    [Fact]
    public void Only_a_provider_answer_of_verified_with_a_provider_earns_the_verified_wording()
    {
        SignInAsVerifier();
        Api.Result = () => Ok.Of(VerificationSample.Result(external: "Verified", provider: "acme-kyc"));

        var cut = RenderDetails();

        cut.WaitForAssertion(() => cut.Find("[data-testid=headline]").TextContent.ShouldContain("Officially verified by acme-kyc"));
        cut.Find("[data-testid=external-card]").TextContent.ShouldContain("ref-1");
    }

    [Fact]
    public void Checks_show_plain_titles_outcome_text_and_severity_not_colour_alone()
    {
        SignInAsVerifier();
        var cut = RenderDetails();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=check-summary]").Count.ShouldBe(1));
        var text = cut.Find("[data-testid=check-summary]").TextContent;
        text.ShouldContain("2 passed");
        var table = cut.FindAll("table").First(t => t.GetAttribute("aria-label") == "Individual checks").TextContent;
        table.ShouldContain("PAN has the expected format");
        table.ShouldContain("Passed");
        table.ShouldContain("Warning");
        table.ShouldContain("Failed");
        table.ShouldContain("Needs review");
        table.ShouldContain("Not checked");
    }

    [Fact]
    public void Review_reasons_and_field_level_matches_are_shown_without_scores()
    {
        SignInAsVerifier();
        Api.Result = () => Ok.Of(VerificationSample.Result(status: "ReviewRequired", review: true));

        var cut = RenderDetails();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=review-reasons]").Count.ShouldBe(1));
        cut.Find("[data-testid=review-reasons]").TextContent.ShouldContain("A check failed.");
        var matches = cut.Find("[data-testid=field-matches]").TextContent;
        matches.ShouldContain("Partly matches");
        matches.ShouldContain("Matches");
        cut.Find("[data-testid=headline]").TextContent.ShouldContain("Needs review");
    }

    [Fact]
    public void Extracted_fields_use_masked_identifiers()
    {
        SignInAsVerifier();

        var cut = RenderDetails();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=extracted-card]").Count.ShouldBe(1));
        var card = cut.Find("[data-testid=extracted-card]").TextContent;
        card.ShouldContain("XXXXX1234X");
        card.ShouldContain("Individual");
    }

    [Fact]
    public void A_passport_shows_check_digit_outcomes_as_passed_or_failed_never_valid()
    {
        SignInAsVerifier();
        var passport = new VerificationExtractedDto(null, null, new PassportExtractedDto("XXXX567", "IND", "IND", "SHARMA", "PRIYA", new DateOnly(1990, 5, 14), "F", null, new DateOnly(2029, 7, 31), false,
            new MrzDto(true, "TD3", new MrzChecksDto("Valid", "Invalid", "Valid", "NotPresent", "Valid"))));
        Api.Result = () => Ok.Of(VerificationSample.Result("Passport", extracted: passport));

        var cut = RenderDetails();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=extracted-card]").Count.ShouldBe(1));
        var text = cut.Find("[data-testid=extracted-card]").TextContent;
        text.ShouldContain("Passed");
        text.ShouldContain("Failed");
        text.ShouldContain("Not present");
        text.ShouldNotContain("Valid");
    }

    [Fact]
    public void A_bank_statement_shows_a_summary_and_a_paged_transactions_table()
    {
        SignInAsVerifier();
        var bank = new VerificationExtractedDto(new BankStatementExtractedDto("Sample Bank", "Priya", "XXXXXX4821", "INR", new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), 1000m, 2000.5m, 120, 5000m, 4000m, "DigitalPdf", "generic", [], false), null, null);
        Api.Result = () => Ok.Of(VerificationSample.Result("BankStatement", external: "NotRequested", extracted: bank));

        var cut = RenderDetails();

        cut.WaitForAssertion(() => cut.FindAll("table[aria-label='Transactions read from the statement'] tbody tr").Count.ShouldBe(1));
        cut.Find("[data-testid=extracted-card]").TextContent.ShouldContain("₹2,000.50");
        cut.Find("[data-testid=extracted-card]").TextContent.ShouldContain("XXXXXX4821");
        cut.Find("[data-testid=external-headline]").TextContent.ShouldContain("No official verification was requested");
        cut.FindAll("[data-testid=next-page]").Last().Click();
        cut.WaitForAssertion(() => Api.Calls.ShouldContain("transactions:2"));
    }

    [Fact]
    public void Credits_show_only_when_the_api_included_them()
    {
        SignInAsVerifier();
        var without = RenderDetails();
        without.WaitForAssertion(() => without.FindAll("[data-testid=stage-stepper]").Count.ShouldBe(1));
        without.FindAll("[data-testid=credits-card]").ShouldBeEmpty();

        Api.Result = () => Ok.Of(VerificationSample.Result(credits: true));
        var with = RenderDetails();
        with.WaitForAssertion(() => with.Find("[data-testid=credits-card]").TextContent.ShouldContain("1 credit"));
        with.Find("[data-testid=credits-card]").TextContent.ShouldContain("2 credits");
    }

    [Fact]
    public void A_purged_check_says_the_details_were_erased()
    {
        SignInAsVerifier();
        Api.Result = () => Ok.Of(VerificationSample.Result(purged: true));

        var cut = RenderDetails();

        cut.WaitForAssertion(() => cut.Find("[data-testid=extracted-card]").TextContent.ShouldContain("were erased"));
    }

    [Fact]
    public void A_failed_check_explains_why_in_plain_words()
    {
        SignInAsVerifier();
        Api.Result = () => Ok.Of(VerificationSample.Result(status: "Failed", errorCode: "DOCUMENT_UNREADABLE"));

        var cut = RenderDetails();

        cut.WaitForAssertion(() => cut.Find("[data-testid=failure]").TextContent.ShouldContain("could not read the text"));
    }

    [Fact]
    public void Retry_provider_check_shows_when_allowed_and_calls_the_api_after_confirmation()
    {
        SignInAsVerifier();
        var providers = Providers();
        Api.Result = () => Ok.Of(VerificationSample.Result(external: "Timeout"));
        var cut = RenderDetails();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=retry-external]").Count.ShouldBe(1));

        cut.Find("[data-testid=retry-external]").Click();
        ConfirmDialog(providers);

        cut.WaitForAssertion(() => Api.Calls.ShouldContain("retry"));
    }

    [Fact]
    public void Retry_is_hidden_without_the_permission_or_when_no_provider_exists()
    {
        SignInAsVerifier(WebPermissions.VerificationPanSubmit);
        Api.Result = () => Ok.Of(VerificationSample.Result(external: "Timeout"));
        var noPermission = RenderDetails();
        noPermission.WaitForAssertion(() => noPermission.FindAll("[data-testid=external-card]").Count.ShouldBe(1));
        noPermission.FindAll("[data-testid=retry-external]").ShouldBeEmpty();

        SignInAsVerifier();
        Api.Result = () => Ok.Of(VerificationSample.Result(external: "ProviderNotConfigured"));
        var noProvider = RenderDetails();
        noProvider.WaitForAssertion(() => noProvider.FindAll("[data-testid=external-card]").Count.ShouldBe(1));
        noProvider.FindAll("[data-testid=retry-external]").ShouldBeEmpty("nothing to retry while no provider is set up");
    }

    [Fact]
    public void Report_download_follows_its_permission()
    {
        SignInAsVerifier();
        var allowed = RenderDetails();
        allowed.WaitForAssertion(() => allowed.FindAll("[data-testid=download-report]").Count.ShouldBe(1));
        allowed.Find("[data-testid=download-report]").GetAttribute("href").ShouldBe($"bff/client/verification/{VerificationSample.Id}/report?format=html");

        SignInAsVerifier(WebPermissions.VerificationReportDownload);
        var denied = RenderDetails();
        denied.WaitForAssertion(() => denied.FindAll("[data-testid=stage-stepper]").Count.ShouldBe(1));
        denied.FindAll("[data-testid=download-report]").ShouldBeEmpty();
    }

    [Fact]
    public void A_running_check_is_polled_passively_then_reloads_and_stops_polling()
    {
        SignInAsVerifier();
        var results = 0;
        Api.Result = () => Ok.Of(results++ == 0 ? VerificationSample.Result(status: "Processing") : VerificationSample.Result());
        Api.Status = n => Ok.Of(VerificationSample.Status(n < 2 ? "Processing" : "Completed"));
        var cut = RenderDetails();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=in-progress]").Count.ShouldBe(1));

        for (var i = 0; i < 10 && cut.FindAll("[data-testid=in-progress]").Count > 0; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(3));
            cut.WaitForState(() => true, TimeSpan.FromMilliseconds(30));
        }

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=in-progress]").ShouldBeEmpty());
        var count = Api.StatusCount;
        Clock.Advance(TimeSpan.FromSeconds(30));
        Api.StatusCount.ShouldBe(count, "polling stops at a terminal state");
        Api.StatusCalls.ShouldAllBe(c => c.Passive);
    }

    [Fact]
    public void Leaving_the_page_stops_the_polling()
    {
        SignInAsVerifier();
        Api.Result = () => Ok.Of(VerificationSample.Result(status: "Processing"));
        Api.Status = _ => Ok.Of(VerificationSample.Status("Processing"));
        var cut = RenderDetails();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=in-progress]").Count.ShouldBe(1));
        Clock.Advance(TimeSpan.FromSeconds(2));
        cut.WaitForAssertion(() => Api.StatusCount.ShouldBeGreaterThan(0));

        ((IDisposable)cut.Instance).Dispose();
        var count = Api.StatusCount;
        Clock.Advance(TimeSpan.FromSeconds(60));

        Api.StatusCount.ShouldBe(count);
    }

    [Fact]
    public void Not_found_and_errors_show_a_reference_and_a_way_back()
    {
        SignInAsVerifier();
        Api.Result = () => Ok.Fail<VerificationResultDto>("NOT_FOUND", "We couldn't find that. It may have been removed.", "corr-404", 404);

        var cut = RenderDetails();

        cut.WaitForAssertion(() => cut.Find("[data-testid=details-error]").TextContent.ShouldContain("could not find this check"));
        cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-404");
        cut.Find("[data-testid=details-error] a").GetAttribute("href").ShouldBe("client/verification/history");
    }
}
