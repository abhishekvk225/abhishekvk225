using Bunit.TestDoubles;
using NexaVerify.Contracts.Common;
using NexaVerify.Web.Components.Billing;
using BillingOrderDetailPage = NexaVerify.Web.Pages.Client.BillingOrderDetail;
using BillingOrdersPage = NexaVerify.Web.Pages.Client.BillingOrders;
using BillingProfilePage = NexaVerify.Web.Pages.Client.BillingProfile;
using BillingReturnPage = NexaVerify.Web.Pages.Client.BillingReturn;
using BuyCreditsPage = NexaVerify.Web.Pages.Client.BuyCredits;
using ClientDashboardPage = NexaVerify.Web.Pages.Client.ClientDashboard;
using DevPayPage = NexaVerify.Web.Pages.Client.DevPay;
using LicensePage = NexaVerify.Web.Pages.Client.License;

namespace NexaVerify.Web.ComponentTests;

public class BuyCreditsPageTests : BillingPageTestBase
{
    private BunitNavigationManager Nav => (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<BuyCreditsPage> RenderReady(IRenderedComponent<MudDialogProvider>? providers = null)
    {
        var cut = Render<BuyCreditsPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pack-card]").Count.ShouldBe(2));
        return cut;
    }

    private static void Confirm(IRenderedComponent<MudDialogProvider> providers)
    {
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=confirm-ok]").Count.ShouldBe(1));
        providers.Find("[data-testid=confirm-ok]").Click();
    }

    [Fact]
    public void Shows_a_skeleton_then_the_packs_with_price_tax_and_total_from_the_api()
    {
        SignInAsBilling();
        var gate = new Gate<ApiResult<IReadOnlyList<CreditPackDto>>>();
        Billing.Packs = () => gate.Task;

        var cut = Render<BuyCreditsPage>();

        cut.FindAll("[data-testid=packs-loading]").Count.ShouldBe(1);
        gate.Release(ApiResult<IReadOnlyList<CreditPackDto>>.Ok([BillingSample.Starter(), BillingSample.Growth()]));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pack-card]").Count.ShouldBe(2));
        var starter = cut.FindAll("[data-testid=pack-card]")[0];
        starter.QuerySelector("[data-testid=price-subtotal]")!.TextContent.ShouldBe("$49.00");
        starter.QuerySelector("[data-testid=price-tax]")!.TextContent.ShouldBe("$9.80");
        starter.QuerySelector("[data-testid=price-total]")!.TextContent.ShouldBe("$58.80");
        starter.TextContent.ShouldContain("VAT (20%)");
        starter.QuerySelector("[data-testid=pack-credits]")!.TextContent.ShouldBe("5,000");
        starter.TextContent.ShouldContain("Valid for 1 year");
    }

    [Fact]
    public void Only_the_cheapest_pack_per_credit_is_marked_best_value()
    {
        SignInAsBilling();

        var cut = RenderReady();

        var cards = cut.FindAll("[data-testid=pack-card]");
        cards[0].QuerySelector("[data-testid=best-value]").ShouldBeNull();
        cards[1].QuerySelector("[data-testid=best-value]").ShouldNotBeNull();
    }

    [Fact]
    public void The_page_explains_what_happens_in_plain_words()
    {
        SignInAsBilling();

        var cut = RenderReady();

        var text = cut.Find("[data-testid=payment-explainer]").TextContent;
        text.ShouldContain("secure payment page");
        text.ShouldContain("added to your account automatically");
        text.ShouldContain("never receives or stores them");
    }

    [Fact]
    public void Someone_who_can_only_read_billing_sees_the_packs_but_no_buy_button()
    {
        SignInAsBillingReader();

        var cut = RenderReady();

        cut.FindAll("[data-testid=buy-pack]").ShouldBeEmpty();
    }

    [Fact]
    public void No_packs_is_an_empty_state_not_an_error()
    {
        SignInAsBilling();
        Billing.Packs = () => Ok.Of<IReadOnlyList<CreditPackDto>>([]);

        var cut = Render<BuyCreditsPage>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=empty-state]").TextContent.ShouldContain("No credit packs"));
    }

    [Fact]
    public void A_failed_load_shows_the_reference_and_retry_reloads()
    {
        SignInAsBilling();
        var attempts = 0;
        Billing.Packs = () => ++attempts == 1 ? Ok.Fail<IReadOnlyList<CreditPackDto>>(correlation: "corr-packs") : Ok.Of<IReadOnlyList<CreditPackDto>>([BillingSample.Starter()]);

        var cut = Render<BuyCreditsPage>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-packs"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Try again")).Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pack-card]").Count.ShouldBe(1));
    }

    [Theory]
    [InlineData(503)]
    [InlineData(403)]
    public void Disabled_online_payments_say_so_kindly_and_point_to_contact(int status)
    {
        SignInAsBilling();
        Billing.Packs = () => Ok.Fail<IReadOnlyList<CreditPackDto>>("BILLING_DISABLED", "off", "corr", status);

        var cut = Render<BuyCreditsPage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=billing-disabled]").Count.ShouldBe(1));
        cut.Markup.ShouldContain("Online payments are not available yet");
        cut.Find("[data-testid=contact-us]").GetAttribute("href").ShouldBe("/contact");
        cut.FindAll("[data-testid=error-state]").ShouldBeEmpty();
        cut.FindAll("[data-testid=buy-pack]").ShouldBeEmpty();
    }

    [Fact]
    public void Incomplete_billing_details_are_pointed_out_with_a_link()
    {
        SignInAsBilling();
        Billing.Profile = () => Ok.Of(BillingSample.EmptyProfile());

        var cut = RenderReady();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=profile-prompt]").Count.ShouldBe(1));
        cut.Find("[data-testid=profile-link]").GetAttribute("href").ShouldBe("client/billing/profile");
    }

    [Fact]
    public void Complete_details_show_no_prompt()
    {
        SignInAsBilling();

        var cut = RenderReady();

        cut.FindAll("[data-testid=profile-prompt]").ShouldBeEmpty();
    }

    [Fact]
    public void Buying_asks_first_then_creates_the_order_and_goes_to_the_payment_page()
    {
        SignInAsBilling();
        var providers = Providers();
        var cut = RenderReady();

        cut.FindAll("[data-testid=buy-pack]")[0].Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=confirm-message]").Count.ShouldBe(1));
        var message = providers.Find("[data-testid=confirm-message]").TextContent;
        message.ShouldContain("$58.80");
        message.ShouldContain("Starter pack");
        message.ShouldContain("never see your card");
        Billing.Calls.ShouldNotContain(c => c.StartsWith("checkout"), "nothing is created before the person agrees");

        Confirm(providers);

        cut.WaitForAssertion(() => Nav.History.Count.ShouldBeGreaterThan(0));
        var visit = Nav.History.First();
        visit.Uri.ShouldBe("https://pay.example.test/session/abc");
        visit.Options.ForceLoad.ShouldBeTrue();
        Billing.Calls.Count(c => c.StartsWith("checkout")).ShouldBe(1);
        Billing.CheckoutKeys.Single().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Cancelling_the_question_does_nothing()
    {
        SignInAsBilling();
        var providers = Providers();
        var cut = RenderReady();

        cut.FindAll("[data-testid=buy-pack]")[0].Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=confirm-cancel]").Count.ShouldBe(1));
        providers.Find("[data-testid=confirm-cancel]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=buy-pack]").All(b => !b.HasAttribute("disabled")).ShouldBeTrue());
        Billing.Calls.ShouldNotContain(c => c.StartsWith("checkout"));
        Nav.History.ShouldBeEmpty();
    }

    [Fact]
    public void A_double_click_starts_one_purchase_only()
    {
        SignInAsBilling();
        var providers = Providers();
        var gate = new Gate<ApiResult<CheckoutResponse>>();
        Billing.Checkout = (_, _) => gate.Task;
        var cut = RenderReady();
        var button = cut.FindAll("[data-testid=buy-pack]")[0];

        button.Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=confirm-ok]").Count.ShouldBe(1));
        cut.FindAll("[data-testid=buy-pack]").ShouldAllBe(b => b.HasAttribute("disabled"), "every Buy button is off while a purchase is starting");
        Confirm(providers);
        cut.WaitForAssertion(() => Billing.Calls.Count(c => c.StartsWith("checkout")).ShouldBe(1));
        cut.FindAll("[data-testid=buy-pack]").ShouldAllBe(b => b.HasAttribute("disabled"));

        gate.Release(ApiResult<CheckoutResponse>.Ok(new CheckoutResponse(BillingSample.OrderId, "https://pay.example.test/s", null)));
        cut.WaitForAssertion(() => Nav.History.Count.ShouldBe(1));
        Billing.Calls.Count(c => c.StartsWith("checkout")).ShouldBe(1);
    }

    [Theory]
    [InlineData("javascript:alert(document.cookie)")]
    [InlineData("http://pay.example.test/insecure")]
    [InlineData("data:text/html,hi")]
    [InlineData("/dev/pay/00000000-0000-0000-0000-00000000d001")]
    [InlineData("//evil.example/x")]
    public void An_unsafe_payment_address_is_never_followed(string url)
    {
        SignInAsBilling();
        var providers = Providers();
        var snacks = Render<MudSnackbarProvider>();
        Billing.Checkout = (_, _) => Ok.Of(new CheckoutResponse(BillingSample.OrderId, url, null));
        var cut = RenderReady();

        cut.FindAll("[data-testid=buy-pack]")[0].Click();
        Confirm(providers);

        snacks.WaitForAssertion(() => snacks.Markup.ShouldContain("could not open the payment page"));
        Nav.History.ShouldBeEmpty();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=buy-pack]").All(b => !b.HasAttribute("disabled")).ShouldBeTrue("the person can try again"));
    }

    [Fact]
    public void In_development_the_simulator_page_is_the_only_local_address_allowed()
    {
        SignInAsBilling();
        Environment.EnvironmentName = "Development";
        var providers = Providers();
        Billing.Checkout = (_, _) => Ok.Of(new CheckoutResponse(BillingSample.OrderId, "/dev/pay/" + BillingSample.OrderId, null));
        var cut = RenderReady();

        cut.FindAll("[data-testid=buy-pack]")[0].Click();
        Confirm(providers);

        cut.WaitForAssertion(() => Nav.History.Count.ShouldBe(1));
        Nav.History.First().Uri.ShouldBe($"http://localhost/dev/pay/{BillingSample.OrderId}");
    }

    [Fact]
    public void Missing_billing_details_at_checkout_lead_to_the_details_form_not_a_dead_end()
    {
        SignInAsBilling();
        var providers = Providers();
        Billing.Checkout = (_, _) => Task.FromResult(ApiResult<CheckoutResponse>.Fail(new ApiError("VALIDATION_FAILED", "Some details are missing.", "corr-422", 422,
            new Dictionary<string, string[]> { ["legalName"] = ["Enter your company name."], ["addressLine1"] = ["Enter your address."] })));
        var cut = RenderReady();

        cut.FindAll("[data-testid=buy-pack]")[0].Click();
        Confirm(providers);

        cut.WaitForAssertion(() => cut.Find("[data-testid=profile-prompt]").TextContent.ShouldContain("Enter your company name."));
        cut.Find("[data-testid=profile-link]").GetAttribute("href").ShouldBe("client/billing/profile");
        Nav.History.ShouldBeEmpty();
    }

    [Fact]
    public void Payments_switched_off_at_checkout_swap_the_page_for_the_friendly_notice()
    {
        SignInAsBilling();
        var providers = Providers();
        Billing.Checkout = (_, _) => Ok.Fail<CheckoutResponse>("BILLING_DISABLED", "off", "c", 503);
        var cut = RenderReady();

        cut.FindAll("[data-testid=buy-pack]")[0].Click();
        Confirm(providers);

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=billing-disabled]").Count.ShouldBe(1));
    }

    [Fact]
    public void A_timeout_keeps_the_same_key_for_the_retry_and_a_definite_refusal_starts_a_new_purchase()
    {
        SignInAsBilling();
        var providers = Providers();
        var snacks = Render<MudSnackbarProvider>();
        var answers = new Queue<ApiResult<CheckoutResponse>>([
            ApiResult<CheckoutResponse>.Fail("API_TIMEOUT", "Slow.", null, 504),
            ApiResult<CheckoutResponse>.Fail("CONFLICT", "You already have a payment in progress.", "c", 409),
            ApiResult<CheckoutResponse>.Fail("CONFLICT", "You already have a payment in progress.", "c", 409),
        ]);
        Billing.Checkout = (_, _) => Task.FromResult(answers.Dequeue());
        var cut = RenderReady();

        for (var i = 1; i <= 3; i++)
        {
            cut.WaitForAssertion(() => cut.FindAll("[data-testid=buy-pack]").All(b => !b.HasAttribute("disabled")).ShouldBeTrue());
            cut.FindAll("[data-testid=buy-pack]")[0].Click();
            Confirm(providers);
            cut.WaitForAssertion(() => Billing.CheckoutKeys.Count.ShouldBe(i));
        }

        Billing.CheckoutKeys[1].ShouldBe(Billing.CheckoutKeys[0], "the first answer was lost: the retry must not buy twice");
        Billing.CheckoutKeys[2].ShouldNotBe(Billing.CheckoutKeys[1], "after a clear 'no' the next try is a new purchase");
        snacks.WaitForAssertion(() => snacks.Markup.ShouldContain("already have a payment in progress"));
    }
}

public class BillingOrdersPageTests : BillingPageTestBase
{
    [Fact]
    public void Shows_a_skeleton_then_orders_with_status_chips_and_invoice_links_only_when_there_is_an_invoice()
    {
        SignInAsBilling();
        var gate = new Gate<ApiResult<PagedResult<OrderListItemDto>>>();
        Billing.Orders = (_, _) => gate.Task;

        var cut = Render<BillingOrdersPage>();

        cut.FindAll("[data-testid=skeleton-table]").Count.ShouldBeGreaterThan(0);
        gate.Release(Ok.Page(BillingSample.Item(), BillingSample.Item(OrderStatuses.Pending, null) with { Id = Guid.NewGuid() }));
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(2));
        var rows = cut.FindAll("table tbody tr");
        rows[0].TextContent.ShouldContain("$58.80");
        rows[0].TextContent.ShouldContain("5,000");
        rows[0].QuerySelector("[data-status='Paid']").ShouldNotBeNull();
        rows[1].QuerySelector("[data-status='Waiting for payment']").ShouldNotBeNull();
        var link = rows[0].QuerySelector("[data-testid=invoice-link]")!;
        link.GetAttribute("href").ShouldBe($"bff/client/billing/orders/{BillingSample.OrderId}/invoice");
        link.GetAttribute("target").ShouldBe("_blank");
        link.GetAttribute("rel")!.ShouldContain("noopener");
        rows[1].QuerySelector("[data-testid=invoice-link]").ShouldBeNull("no invoice until the payment is confirmed");
    }

    [Fact]
    public void No_orders_is_a_friendly_empty_state()
    {
        SignInAsBilling();
        Billing.Orders = (_, _) => Task.FromResult(Ok.Page<OrderListItemDto>());

        var cut = Render<BillingOrdersPage>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=empty-state]").TextContent.ShouldContain("have not bought any credits"));
    }

    [Fact]
    public void A_failed_load_shows_the_reference_and_can_retry()
    {
        SignInAsBilling();
        var attempts = 0;
        Billing.Orders = (_, _) => ++attempts == 1 ? Ok.Fail<PagedResult<OrderListItemDto>>(correlation: "corr-orders") : Task.FromResult(Ok.Page(BillingSample.Item()));

        var cut = Render<BillingOrdersPage>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-orders"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Try again")).Click();
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));
    }

    [Fact]
    public async Task The_status_filter_asks_the_api_for_that_status()
    {
        SignInAsBilling();
        var cut = Render<BillingOrdersPage>();
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));

        await cut.InvokeAsync(() => cut.FindComponent<MudSelect<string>>().Instance.ValueChanged.InvokeAsync(OrderStatuses.Failed));

        cut.WaitForAssertion(() => Billing.Calls.ShouldContain("orders:Failed:1"));
    }

    [Fact]
    public void Export_goes_through_the_portal_and_buying_needs_the_manage_permission()
    {
        SignInAsBilling();
        var manager = Render<BillingOrdersPage>();
        manager.WaitForAssertion(() => manager.FindAll("[data-testid=download-csv]").Count.ShouldBe(1));
        manager.Find("[data-testid=download-csv]").GetAttribute("href")!.ShouldStartWith("bff/client/billing/orders/export.csv?from=2026-05-17&to=2026-06-15");
        manager.FindAll("[data-testid=buy-credits]").Count.ShouldBe(1);

        SignInAsBillingReader();
        var reader = Render<BillingOrdersPage>();
        reader.WaitForAssertion(() => reader.FindAll("table tbody tr").Count.ShouldBe(1));
        reader.FindAll("[data-testid=buy-credits]").ShouldBeEmpty();
    }

    [Fact]
    public void Narrow_screens_get_cards_with_the_same_actions()
    {
        SignInAsBilling();

        var cut = Render<BillingOrdersPage>();

        cut.WaitForAssertion(() => cut.FindAll(".nv-cards .nv-row-card").Count.ShouldBe(1));
        cut.Find(".nv-row-card").TextContent.ShouldContain("Starter pack");
    }
}

public class BillingOrderDetailPageTests : BillingPageTestBase
{
    [Fact]
    public void A_paid_order_shows_the_breakdown_the_invoice_and_the_license_link()
    {
        SignInAsBilling();
        var licenseId = Guid.Parse("00000000-0000-0000-0000-00000000f001");

        var cut = Render<BillingOrderDetailPage>(p => p.Add(x => x.Id, BillingSample.OrderId));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=order-card]").Count.ShouldBe(1));
        cut.Find("[data-testid=price-total]").TextContent.ShouldBe("$58.80");
        cut.Find("[data-testid=invoice-link]").GetAttribute("href").ShouldBe($"bff/client/billing/orders/{BillingSample.OrderId}/invoice");
        cut.Find("[data-testid=license-link]").GetAttribute("href").ShouldBe($"client/license/{licenseId}");
        cut.Find("[data-testid=order-card]").TextContent.ShouldContain("NV-2026-001042");
    }

    [Fact]
    public void A_pending_order_explains_it_is_waiting_and_has_no_invoice()
    {
        SignInAsBilling();
        Billing.Order = _ => Ok.Of(BillingSample.Order(OrderStatuses.Pending));

        var cut = Render<BillingOrderDetailPage>(p => p.Add(x => x.Id, BillingSample.OrderId));

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("waiting for the payment partner"));
        cut.FindAll("[data-testid=invoice-link]").ShouldBeEmpty();
        cut.FindAll("[data-testid=license-link]").ShouldBeEmpty();
    }

    [Fact]
    public void An_order_that_is_not_yours_reads_as_not_found_with_a_reference_and_retry()
    {
        SignInAsBilling();
        Billing.Order = _ => Ok.Fail<OrderDto>("NOT_FOUND", "We couldn't find that. It may have been removed.", "corr-404", 404);

        var cut = Render<BillingOrderDetailPage>(p => p.Add(x => x.Id, Guid.NewGuid()));

        cut.WaitForAssertion(() => cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-404"));
        cut.Markup.ShouldContain("couldn't find that");
    }
}

public class BillingProfilePageTests : BillingPageTestBase
{
    private IRenderedComponent<BillingProfilePage> RenderLoaded()
    {
        var cut = Render<BillingProfilePage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=profile-card]").Count.ShouldBe(1));
        return cut;
    }

    [Fact]
    public void Shows_a_skeleton_then_the_saved_details()
    {
        SignInAsBilling();
        var gate = new Gate<ApiResult<BillingProfileDto>>();
        Billing.Profile = () => gate.Task;

        var cut = Render<BillingProfilePage>();

        cut.FindAll("[data-testid=skeleton-card]").Count.ShouldBe(1);
        gate.Release(ApiResult<BillingProfileDto>.Ok(BillingSample.CompleteProfile()));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=profile-card]").Count.ShouldBe(1));
        cut.FindAll("input").Any(i => i.GetAttribute("value") == "Acme Corp Ltd").ShouldBeTrue("the saved company name is in the form");
    }

    [Fact]
    public void A_failed_load_shows_the_reference()
    {
        SignInAsBilling();
        Billing.Profile = () => Ok.Fail<BillingProfileDto>(correlation: "corr-prof");

        var cut = Render<BillingProfilePage>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-prof"));
    }

    [Fact]
    public void The_tax_number_field_explains_itself()
    {
        SignInAsBilling();

        var cut = RenderLoaded();

        cut.Markup.ShouldContain("VAT, GST or other tax registration number");
        cut.Markup.ShouldContain("printed on your invoices");
    }

    [Fact]
    public void Required_details_are_checked_before_anything_is_sent()
    {
        SignInAsBilling();
        Billing.Profile = () => Ok.Of(BillingSample.EmptyProfile());
        var cut = RenderLoaded();

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Enter the name of your company"));
        cut.Markup.ShouldContain("Enter your city.");
        cut.Markup.ShouldContain("two-letter country code");
        cut.Markup.ShouldContain("Enter the email address that should receive invoices.");
        Billing.Calls.ShouldNotContain("save-profile");
    }

    [Fact]
    public void A_bad_country_or_email_is_explained_inline()
    {
        SignInAsBilling();
        var cut = RenderLoaded();
        Fill(cut, "Country code", "United Kingdom");
        Fill(cut, "Invoice email", "not-an-email");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Use the two-letter country code"));
        cut.Markup.ShouldContain("Enter a valid email address.");
        Billing.Calls.ShouldNotContain("save-profile");
    }

    [Fact]
    public void Saving_sends_tidy_details_with_the_version_and_confirms_in_a_snackbar()
    {
        SignInAsBilling();
        var snacks = Render<MudSnackbarProvider>();
        var cut = RenderLoaded();
        Fill(cut, "Company name (legal name)", "  Acme Holdings  ");
        Fill(cut, "Country code", "in");
        Fill(cut, "Tax number (optional)", "27AAAAA0000A1Z5");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Billing.LastSave.ShouldNotBeNull());
        var sent = Billing.LastSave!;
        (sent.LegalName, sent.Country, sent.TaxId, sent.RowVersion).ShouldBe(("Acme Holdings", "IN", "27AAAAA0000A1Z5", "AAAAAAAB"));
        snacks.WaitForAssertion(() => snacks.Markup.ShouldContain("billing details were saved"));
    }

    [Fact]
    public void Field_errors_from_the_api_land_on_the_fields()
    {
        SignInAsBilling();
        Billing.Save = _ => Task.FromResult(ApiResult<BillingProfileDto>.Fail(new ApiError("VALIDATION_FAILED", "Some details are not valid.", "corr-v", 422,
            new Dictionary<string, string[]> { ["taxId"] = ["That tax number is not valid for your country."] })));
        var cut = RenderLoaded();

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("That tax number is not valid for your country."));
    }

    [Fact]
    public void A_clash_with_another_editor_is_reported_with_the_reference()
    {
        SignInAsBilling();
        Billing.Save = _ => Ok.Fail<BillingProfileDto>("CONCURRENCY_CONFLICT", "Someone else changed this at the same time. Reload and try again.", "corr-409", 409);
        var cut = RenderLoaded();

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find("[data-testid=form-error]").TextContent.ShouldContain("corr-409"));
        cut.Find("[data-testid=form-error]").TextContent.ShouldContain("Someone else changed this");
    }

    [Fact]
    public void A_read_only_user_can_look_but_not_change()
    {
        SignInAsBillingReader();

        var cut = RenderLoaded();

        cut.FindAll("[data-testid=save-profile]").ShouldBeEmpty();
        cut.FindAll("[data-testid=read-only]").Count.ShouldBe(1);
        cut.FindAll("input[readonly]").Count.ShouldBeGreaterThan(5);
    }
}

public class BillingReturnPageTests : BillingPageTestBase
{
    /// <summary>The way a real arrival works: the address carries the query, the page reads it from there.</summary>
    private IRenderedComponent<BillingReturnPage> RenderReturn(string? result = "success", Guid? order = null)
    {
        var query = $"?order={order ?? BillingSample.OrderId}" + (result is null ? string.Empty : $"&result={result}");
        Services.GetRequiredService<NavigationManager>().NavigateTo("/client/billing/return" + query);
        return Render<BillingReturnPage>();
    }

    [Fact]
    public void Checking_is_shown_while_the_first_answer_is_on_its_way()
    {
        SignInAsBilling();
        var gate = new Gate<ApiResult<OrderDto>>();
        Billing.Order = _ => gate.Task;

        var cut = RenderReturn();

        cut.Find("[data-testid=return-checking]").TextContent.ShouldContain("Confirming your payment");
        cut.Find("[data-testid=return-panel]").GetAttribute("data-state").ShouldBe("checking");
    }

    [Fact]
    public void The_address_saying_success_is_not_proof_a_pending_order_stays_pending()
    {
        SignInAsBilling();
        Billing.Order = _ => Ok.Of(BillingSample.Order(OrderStatuses.Pending));

        var cut = RenderReturn("success");

        cut.WaitForAssertion(() => cut.Find("[data-testid=return-pending]").TextContent.ShouldContain("Confirming your payment"));
        cut.FindAll("[data-testid=return-paid]").ShouldBeEmpty();
        cut.Markup.ShouldNotContain("Your credits were added");
    }

    [Fact]
    public void A_paid_order_is_celebrated_with_links_to_the_license_and_the_invoice()
    {
        SignInAsBilling();

        var cut = RenderReturn("success");

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=return-paid]").Count.ShouldBe(1));
        var text = cut.Find("[data-testid=return-paid]").TextContent;
        text.ShouldContain("Your credits were added");
        text.ShouldContain("5,000 credits");
        text.ShouldContain("$58.80");
        cut.Find("[data-testid=see-credits]").GetAttribute("href").ShouldBe("client/license/00000000-0000-0000-0000-00000000f001");
        cut.Find("[data-testid=invoice-link]").GetAttribute("href")!.ShouldContain(BillingSample.OrderId.ToString());
    }

    [Fact]
    public void The_address_saying_cancelled_does_not_stop_a_paid_order_from_showing_as_paid()
    {
        SignInAsBilling();

        var cut = RenderReturn("cancelled");

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=return-paid]").Count.ShouldBe(1));
    }

    [Fact]
    public void Pending_becomes_paid_after_a_few_checks_with_a_growing_delay_and_passive_calls()
    {
        SignInAsBilling();
        Billing.Order = n => Ok.Of(n < 3 ? BillingSample.Order(OrderStatuses.Pending) : BillingSample.Order());

        var cut = RenderReturn();
        cut.WaitForAssertion(() => Billing.OrderCalls.ShouldBe(1));
        cut.FindAll("[data-testid=return-paid]").ShouldBeEmpty();

        Clock.Advance(TimeSpan.FromSeconds(1));
        cut.WaitForAssertion(() => Billing.OrderCalls.ShouldBe(2));
        Clock.Advance(TimeSpan.FromSeconds(1));
        Billing.OrderCalls.ShouldBe(2, "the second wait is two seconds");
        Clock.Advance(TimeSpan.FromSeconds(1));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=return-paid]").Count.ShouldBe(1));
        Billing.OrderCalls.ShouldBe(3, "polling stops as soon as the order is settled");
        Billing.PassiveFlags.ShouldAllBe(p => p, "waiting must not keep the session alive");
    }

    [Theory]
    [InlineData(OrderStatuses.Failed, "did not go through")]
    [InlineData(OrderStatuses.Cancelled, "was cancelled")]
    [InlineData(OrderStatuses.Expired, "expired")]
    public void A_payment_that_did_not_happen_says_what_to_do_next(string status, string title)
    {
        SignInAsBilling();
        Billing.Order = _ => Ok.Of(BillingSample.Order(status));

        var cut = RenderReturn("cancelled");

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=return-failed]").Count.ShouldBe(1));
        cut.Find("[data-testid=return-failed]").TextContent.ShouldContain(title);
        cut.Find("[data-testid=try-again]").GetAttribute("href").ShouldBe("client/billing/buy");
        cut.FindAll("[data-testid=see-credits]").ShouldBeEmpty();
    }

    [Fact]
    public void After_about_a_minute_it_says_the_confirmation_is_slow_and_offers_to_check_again()
    {
        SignInAsBilling();
        Billing.Order = _ => Ok.Of(BillingSample.Order(OrderStatuses.Pending));
        var cut = RenderReturn();

        for (var i = 0; i < 80 && cut.FindAll("[data-testid=check-again]").Count == 0; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            cut.WaitForState(() => true, TimeSpan.FromMilliseconds(30));
        }

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=check-again]").Count.ShouldBe(1));
        cut.Find("[data-testid=return-pending]").TextContent.ShouldContain("do not need to pay again");
        Billing.OrderCalls.ShouldBeInRange(8, 20);

        var before = Billing.OrderCalls;
        Billing.Order = _ => Ok.Of(BillingSample.Order());
        cut.Find("[data-testid=check-again]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=return-paid]").Count.ShouldBe(1));
        Billing.OrderCalls.ShouldBeGreaterThan(before);
    }

    [Fact]
    public void An_order_that_cannot_be_found_stops_at_once_with_the_reference()
    {
        SignInAsBilling();
        Billing.Order = _ => Ok.Fail<OrderDto>("NOT_FOUND", "We couldn't find that. It may have been removed.", "corr-nf", 404);

        var cut = RenderReturn();

        cut.WaitForAssertion(() => cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-nf"));
        Clock.Advance(TimeSpan.FromSeconds(30));
        Billing.OrderCalls.ShouldBe(1, "a definite answer is not polled again");
        cut.Find("[data-testid=return-panel]").GetAttribute("data-state").ShouldBe("error");
    }

    [Fact]
    public void A_short_outage_is_ridden_out_quietly()
    {
        SignInAsBilling();
        Billing.Order = n => n == 1 ? Ok.Fail<OrderDto>("API_UNAVAILABLE", "down", null, 503) : Ok.Of(BillingSample.Order());
        var cut = RenderReturn();

        cut.WaitForAssertion(() => Billing.OrderCalls.ShouldBe(1));
        cut.FindAll("[data-testid=error-state]").ShouldBeEmpty("a hiccup is not shown as an error while we are still trying");
        Clock.Advance(TimeSpan.FromSeconds(1));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=return-paid]").Count.ShouldBe(1));
    }

    [Fact]
    public void A_return_without_an_order_does_not_guess()
    {
        SignInAsBilling();

        Services.GetRequiredService<NavigationManager>().NavigateTo("/client/billing/return?result=success");
        var cut = Render<BillingReturnPage>();

        cut.Find("[data-testid=empty-state]").TextContent.ShouldContain("could not tell which payment");
        Billing.OrderCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Leaving_the_page_stops_the_polling()
    {
        SignInAsBilling();
        Billing.Order = _ => Ok.Of(BillingSample.Order(OrderStatuses.Pending));
        var cut = RenderReturn();
        cut.WaitForAssertion(() => Billing.OrderCalls.ShouldBe(1));

        await cut.InvokeAsync(() => cut.Instance.Dispose());
        Clock.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(50);

        Billing.OrderCalls.ShouldBe(1);
    }
}

public class DevPayPageTests : BillingPageTestBase
{
    private BunitNavigationManager Nav => (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

    [Fact]
    public void Outside_development_the_page_does_not_exist_and_calls_nothing()
    {
        SignInAsBilling();
        Environment.EnvironmentName = "Production";

        var cut = Render<DevPayPage>(p => p.Add(x => x.OrderId, BillingSample.OrderId));

        cut.Markup.ShouldContain("could not find that page");
        cut.FindAll("[data-testid=dev-pay-success]").ShouldBeEmpty();
        Billing.OrderCalls.ShouldBe(0);
        DevBilling.Outcomes.ShouldBeEmpty();
    }

    [Fact]
    public void In_development_it_is_clearly_labelled_and_simulates_a_successful_payment()
    {
        SignInAsBilling();
        Environment.EnvironmentName = "Development";
        Billing.Order = _ => Ok.Of(BillingSample.Order(OrderStatuses.Pending));

        var cut = Render<DevPayPage>(p => p.Add(x => x.OrderId, BillingSample.OrderId));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=dev-pay-card]").Count.ShouldBe(1));
        cut.Find("[data-testid=dev-banner]").TextContent.ShouldContain("simulation");
        cut.Find("[data-testid=dev-pay-success]").Click();

        cut.WaitForAssertion(() => DevBilling.Outcomes.ShouldBe(["success"]));
        cut.WaitForAssertion(() => Nav.History.Count.ShouldBe(1));
        Nav.History.First().Uri.ShouldBe($"client/billing/return?order={BillingSample.OrderId}&result=success");
    }

    [Theory]
    [InlineData("dev-pay-fail", "failed", "cancelled")]
    [InlineData("dev-pay-cancel", "cancelled", "cancelled")]
    public void Failure_and_cancel_are_simulated_too(string button, string outcome, string hint)
    {
        SignInAsBilling();
        Environment.EnvironmentName = "Testing";
        Billing.Order = _ => Ok.Of(BillingSample.Order(OrderStatuses.Pending));
        var cut = Render<DevPayPage>(p => p.Add(x => x.OrderId, BillingSample.OrderId));
        cut.WaitForAssertion(() => cut.FindAll($"[data-testid={button}]").Count.ShouldBe(1));

        cut.Find($"[data-testid={button}]").Click();

        cut.WaitForAssertion(() => DevBilling.Outcomes.ShouldBe([outcome]));
        cut.WaitForAssertion(() => Nav.History.First().Uri.ShouldEndWith($"result={hint}"));
    }

    [Fact]
    public void An_order_that_is_already_settled_cannot_be_paid_again()
    {
        SignInAsBilling();
        Environment.EnvironmentName = "Development";

        var cut = Render<DevPayPage>(p => p.Add(x => x.OrderId, BillingSample.OrderId));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=dev-pay-card]").Count.ShouldBe(1));
        cut.Find("[data-testid=dev-pay-success]").HasAttribute("disabled").ShouldBeTrue();
    }
}

public class BuyCreditsBannerTests : BillingPageTestBase
{
    private static readonly DateTimeOffset Now = BillingSample.Now;

    [Theory]
    [InlineData(900, 1000, 120, false)]
    [InlineData(200, 1000, 120, true)]    // low balance
    [InlineData(50, 1000, 120, true)]
    [InlineData(900, 1000, 3, true)]      // ending soon
    [InlineData(1000, 1000, 14, false)]   // a fresh trial is healthy
    public void The_call_to_action_appears_only_when_the_balance_or_time_is_getting_short(long remaining, long total, int days, bool shown)
    {
        SignInAsBilling();

        var cut = Render<BuyCreditsBanner>(p => p.Add(x => x.Remaining, remaining).Add(x => x.Total, total).Add(x => x.ExpiresAt, Now.AddDays(days)).Add(x => x.Now, Now));

        cut.FindAll("[data-testid=buy-credits-banner]").Count.ShouldBe(shown ? 1 : 0);
        if (shown)
        {
            cut.Find("[data-testid=buy-credits-cta]").GetAttribute("href").ShouldBe("client/billing/buy");
        }
    }

    [Fact]
    public void People_who_cannot_buy_are_not_shown_a_button_they_cannot_use()
    {
        SignInAsBillingReader();

        var cut = Render<BuyCreditsBanner>(p => p.Add(x => x.Remaining, 10).Add(x => x.Total, 1000).Add(x => x.Now, Now));

        cut.FindAll("[data-testid=buy-credits-banner]").ShouldBeEmpty();
    }

    [Fact]
    public void A_fresh_trial_on_the_license_page_reads_as_healthy_with_no_nudge()
    {
        SignInAsBilling(WebPermissions.LicenseRead);
        Clock.SetUtcNow(BillingSample.Now);
        ClientLicense.Summary = () => Ok.Of(ClientSample.Summary() with { RemainingCredits = 200, TotalCredits = 200, ConsumedCredits = 0, UsablePercent = 100, NextExpiry = Sample.Now.AddDays(14), DaysUntilExpiry = 14 });

        var cut = Render<LicensePage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=license-gauge]").Count.ShouldBe(1));
        cut.Find("[data-testid=license-gauge]").GetAttribute("data-level").ShouldBe("Healthy");
        cut.Find("[data-testid=gauge-help]").TextContent.ShouldNotContain("running lower than usual");
        cut.FindAll("[data-testid=buy-credits-banner]").ShouldBeEmpty();
    }

    [Fact]
    public void A_low_balance_on_the_dashboard_offers_to_buy()
    {
        SignInAsBilling(WebPermissions.DashboardClient);
        Clock.SetUtcNow(BillingSample.Now);
        Dashboard.Client = _ => Ok.Of(Sample.ClientDashboard() with { CreditsRemaining = 80, CreditsTotal = 1000 });

        var cut = Render<ClientDashboardPage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=buy-credits-banner]").Count.ShouldBe(1));
    }
}

public class BillingNavigationTests
{
    [Fact]
    public void The_client_menu_has_a_billing_group_trimmed_by_permission()
    {
        var manager = NavigationCatalog.Trim(NavigationCatalog.Client, p => p is WebPermissions.BillingRead or WebPermissions.BillingManage).Single(g => g.Title == "Billing");
        manager.Items.Select(i => i.Href).ShouldBe(["client/billing/buy", "client/billing/orders", "client/billing/profile"]);

        var reader = NavigationCatalog.Trim(NavigationCatalog.Client, p => p == WebPermissions.BillingRead).Single(g => g.Title == "Billing");
        reader.Items.Select(i => i.Title).ShouldBe(["Orders & invoices", "Billing details"]);

        NavigationCatalog.Trim(NavigationCatalog.Client, _ => false).ShouldNotContain(g => g.Title == "Billing");
        NavigationCatalog.Trim(NavigationCatalog.Client, p => p != WebPermissions.BillingRead && p != WebPermissions.BillingManage).ShouldNotContain(g => g.Title == "Billing");
    }

    [Fact]
    public void The_admin_menu_has_a_billing_group_for_packs_and_orders()
    {
        var staff = NavigationCatalog.Trim(NavigationCatalog.Admin, p => p is WebPermissions.BillingPacksManage or WebPermissions.BillingOrdersRead).Single(g => g.Title == "Billing");
        staff.Items.Select(i => i.Href).ShouldBe(["admin/billing/packs", "admin/billing/orders"]);

        NavigationCatalog.Trim(NavigationCatalog.Admin, p => p == WebPermissions.BillingOrdersRead).Single(g => g.Title == "Billing").Items.Single().Href.ShouldBe("admin/billing/orders");
        NavigationCatalog.Trim(NavigationCatalog.Admin, p => p == WebPermissions.ClientsRead).ShouldNotContain(g => g.Title == "Billing");
    }

    [Fact]
    public void Every_billing_page_requires_its_permission_and_the_dev_page_requires_manage()
    {
        string[] Policy(Type page) => page.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Select(a => a.Policy!).ToArray();

        Policy(typeof(NexaVerify.Web.Pages.Client.BuyCredits)).ShouldContain("perm:billing.read");
        Policy(typeof(NexaVerify.Web.Pages.Client.BillingOrders)).ShouldContain("perm:billing.read");
        Policy(typeof(NexaVerify.Web.Pages.Client.BillingProfile)).ShouldContain("perm:billing.read");
        Policy(typeof(NexaVerify.Web.Pages.Client.BillingReturn)).ShouldContain("perm:billing.read");
        Policy(typeof(NexaVerify.Web.Pages.Client.DevPay)).ShouldContain("perm:billing.manage");
        Policy(typeof(NexaVerify.Web.Pages.Admin.CreditPacks)).ShouldContain("perm:billing.packs.manage");
        Policy(typeof(NexaVerify.Web.Pages.Admin.BillingOrders)).ShouldContain("perm:billing.orders.read");
        Policy(typeof(NexaVerify.Web.Pages.Admin.BillingOrderDetail)).ShouldContain("perm:billing.orders.read");
    }
}
