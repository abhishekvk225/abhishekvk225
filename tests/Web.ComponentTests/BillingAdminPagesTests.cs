using NexaVerify.Contracts.Common;
using NexaVerify.Web.Components.Billing;
using AdminBillingOrderDetailPage = NexaVerify.Web.Pages.Admin.BillingOrderDetail;
using AdminBillingOrdersPage = NexaVerify.Web.Pages.Admin.BillingOrders;
using CreditPacksPage = NexaVerify.Web.Pages.Admin.CreditPacks;

namespace NexaVerify.Web.ComponentTests;

public class CreditPacksPageTests : BillingPageTestBase
{
    private IRenderedComponent<MudDialogProvider> _providers = null!;

    private IRenderedComponent<CreditPacksPage> RenderReady()
    {
        SignInAsBillingStaff(WebPermissions.BillingPacksManage);
        _providers = Providers();
        var cut = Render<CreditPacksPage>();
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(2));
        return cut;
    }

    private void Type(string label, string value) =>
        _providers.FindComponents<MudTextField<string>>().Single(t => t.Instance.Label == label).Find("input,textarea").Input(value);

    private void PriceTyped(string value) =>
        _providers.FindComponents<MudTextField<string>>().Single(t => t.Instance.Label!.StartsWith("Price before tax")).Find("input").Input(value);

    private void Submit() => _providers.Find("[data-testid=form-submit]").Click();

    [Fact]
    public void Shows_a_skeleton_then_the_packs_with_formatted_prices_and_availability()
    {
        SignInAsBillingStaff(WebPermissions.BillingPacksManage);
        var gate = new Gate<ApiResult<IReadOnlyList<AdminPackDto>>>();
        AdminBilling.Packs = () => gate.Task;

        var cut = Render<CreditPacksPage>();

        cut.FindAll("[data-testid=skeleton-table]").Count.ShouldBeGreaterThan(0);
        gate.Release(ApiResult<IReadOnlyList<AdminPackDto>>.Ok([BillingSample.AdminPack(), BillingSample.AdminPack("Yen pack") with { Currency = "JPY", PriceMinor = 1500 }]));
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(2));
        var rows = cut.FindAll("table tbody tr");
        rows[0].TextContent.ShouldContain("$49.00");
        rows[1].TextContent.ShouldContain("¥1,500");
        rows[0].QuerySelector("[data-status='Active']").ShouldNotBeNull();
    }

    [Fact]
    public void No_packs_is_an_empty_state_and_a_failed_load_shows_the_reference()
    {
        SignInAsBillingStaff(WebPermissions.BillingPacksManage);
        AdminBilling.Packs = () => Ok.Of<IReadOnlyList<AdminPackDto>>([]);
        var empty = Render<CreditPacksPage>();
        empty.WaitForAssertion(() => empty.Find("[data-testid=empty-state]").TextContent.ShouldContain("No credit packs yet"));

        AdminBilling.Packs = () => Ok.Fail<IReadOnlyList<AdminPackDto>>(correlation: "corr-packs");
        var failed = Render<CreditPacksPage>();
        failed.WaitForAssertion(() => failed.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-packs"));
    }

    [Fact]
    public void A_new_pack_converts_the_typed_price_to_minor_units_exactly()
    {
        var cut = RenderReady();
        cut.Find("[data-testid=new-pack]").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));

        Submit();
        _providers.Markup.ShouldContain("Give the pack a name.");
        AdminBilling.Calls.ShouldBeEmpty();

        Type("Pack name", "Growth pack");
        PriceTyped("199.9");
        Type("Highlights (one per line)", "25,000 credits\nPriority support");
        Submit();

        cut.WaitForAssertion(() => AdminBilling.Calls.ShouldContain("pack-create:Growth pack"));
        var sent = AdminBilling.LastPack!;
        (sent.PriceMinor, sent.Currency, sent.Credits, sent.IsActive, sent.IsPublic).ShouldBe((19990L, "USD", 1000, true, true));
        sent.Highlights.ShouldBe(["25,000 credits", "Priority support"]);
    }

    [Theory]
    [InlineData("12.345", "at most 2 decimals")]
    [InlineData("free", "at most 2 decimals")]
    [InlineData("0", "more than zero")]
    public void A_price_with_the_wrong_shape_is_explained_and_nothing_is_sent(string price, string message)
    {
        var cut = RenderReady();
        cut.Find("[data-testid=new-pack]").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));
        Type("Pack name", "X");
        PriceTyped(price);

        Submit();

        _providers.WaitForAssertion(() => _providers.Markup.ShouldContain(message));
        AdminBilling.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Yen_take_whole_numbers_and_kuwaiti_dinar_three_decimals()
    {
        var cut = RenderReady();
        cut.Find("[data-testid=new-pack]").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));
        await _providers.InvokeAsync(() => _providers.FindComponent<MudSelect<string>>().Instance.ValueChanged.InvokeAsync("JPY"));
        Type("Pack name", "Yen pack");
        PriceTyped("1500.50");
        Submit();
        _providers.WaitForAssertion(() => _providers.Markup.ShouldContain("whole number of JPY"));
        AdminBilling.Calls.ShouldBeEmpty();

        PriceTyped("1500");
        Submit();
        cut.WaitForAssertion(() => AdminBilling.LastPack.ShouldNotBeNull());
        (AdminBilling.LastPack!.PriceMinor, AdminBilling.LastPack.Currency).ShouldBe((1500L, "JPY"));
    }

    [Fact]
    public void Editing_starts_from_the_saved_pack_and_sends_the_row_version()
    {
        var cut = RenderReady();

        cut.Find("[aria-label='Edit pack Starter pack']").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));
        _providers.FindComponents<MudTextField<string>>().Single(t => t.Instance.Label!.StartsWith("Price before tax")).Find("input").GetAttribute("value").ShouldBe("49.00");
        PriceTyped("59");
        Submit();

        cut.WaitForAssertion(() => AdminBilling.Calls.ShouldContain("pack-update:Starter pack:True"));
        (AdminBilling.LastPack!.PriceMinor, AdminBilling.LastPack.RowVersion).ShouldBe((5900L, "AAAAAAAC"));
    }

    [Fact]
    public void Taking_a_pack_off_sale_and_putting_it_back_both_ask_first()
    {
        var cut = RenderReady();

        cut.FindAll("[data-testid=toggle-pack]")[0].Click();
        _providers.WaitForAssertion(() => _providers.Find("[data-testid=confirm-message]").TextContent.ShouldContain("no longer be offered"));
        AdminBilling.Calls.ShouldBeEmpty();
        _providers.Find("[data-testid=confirm-ok]").Click();
        cut.WaitForAssertion(() => AdminBilling.Calls.ShouldContain("pack-update:Starter pack:False"));

        cut.FindAll("[data-testid=toggle-pack]")[1].Click();
        _providers.WaitForAssertion(() => _providers.Find("[data-testid=confirm-message]").TextContent.ShouldContain("show up for clients"));
        _providers.Find("[data-testid=confirm-ok]").Click();
        cut.WaitForAssertion(() => AdminBilling.Calls.ShouldContain("pack-update:Retired pack:True"));
    }

    [Fact]
    public void Field_errors_from_the_api_stay_in_the_dialog_and_nothing_closes()
    {
        var cut = RenderReady();
        AdminBilling.CreatePack = _ => Task.FromResult(ApiResult<AdminPackDto>.Fail(new ApiError("VALIDATION_FAILED", "Some details are not valid.", "corr-pk", 422,
            new Dictionary<string, string[]> { ["name"] = ["A pack with this name already exists."] })));
        cut.Find("[data-testid=new-pack]").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));
        Type("Pack name", "Dup");
        PriceTyped("10");

        Submit();

        _providers.WaitForAssertion(() => _providers.Markup.ShouldContain("A pack with this name already exists."));
        _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1);
    }
}

public class AdminBillingOrdersPageTests : BillingPageTestBase
{
    [Fact]
    public void Shows_a_skeleton_then_orders_with_the_client_name_and_status()
    {
        SignInAsBillingStaff(WebPermissions.BillingOrdersRead);
        var gate = new Gate<ApiResult<PagedResult<AdminOrderListItemDto>>>();
        AdminBilling.Orders = _ => gate.Task;

        var cut = Render<AdminBillingOrdersPage>();

        cut.FindAll("[data-testid=skeleton-table]").Count.ShouldBeGreaterThan(0);
        gate.Release(Ok.Page(BillingSample.AdminItem(), BillingSample.AdminItem(OrderStatuses.Refunded) with { Id = Guid.NewGuid() }));
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(2));
        var first = cut.FindAll("table tbody tr")[0];
        first.TextContent.ShouldContain("Acme Corp");
        first.TextContent.ShouldContain("$58.80");
        first.QuerySelector("[data-status='Paid']").ShouldNotBeNull();
        cut.FindAll("table tbody tr")[1].QuerySelector("[data-status='Refunded']").ShouldNotBeNull();
        cut.FindAll("[aria-label^='Open order']").Count.ShouldBe(4, "two orders, each as a table row and as a card on narrow screens");
    }

    [Fact]
    public void Empty_and_error_states_are_covered()
    {
        SignInAsBillingStaff(WebPermissions.BillingOrdersRead);
        AdminBilling.Orders = _ => Task.FromResult(Ok.Page<AdminOrderListItemDto>());
        var empty = Render<AdminBillingOrdersPage>();
        empty.WaitForAssertion(() => empty.Find("[data-testid=empty-state]").TextContent.ShouldContain("No orders match"));

        AdminBilling.Orders = _ => Ok.Fail<PagedResult<AdminOrderListItemDto>>(correlation: "corr-aord");
        var failed = Render<AdminBillingOrdersPage>();
        failed.WaitForAssertion(() => failed.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-aord"));
    }

    [Fact]
    public async Task Filters_are_sent_to_the_api_and_can_be_cleared()
    {
        SignInAsBillingStaff(WebPermissions.BillingOrdersRead);
        var cut = Render<AdminBillingOrdersPage>();
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));
        cut.FindAll("[data-testid=clear-filters]").ShouldBeEmpty();

        await cut.InvokeAsync(() => cut.FindComponents<MudSelect<string>>().First(s => s.Instance.Label == "Status").Instance.ValueChanged.InvokeAsync(OrderStatuses.Refunded));
        cut.WaitForAssertion(() => AdminBilling.LastQuery!.Status.ShouldBe(OrderStatuses.Refunded));
        await cut.InvokeAsync(() => cut.FindComponents<MudDatePicker>().First(d => d.Instance.Label == "From").Instance.DateChanged.InvokeAsync(new DateTime(2026, 6, 1)));
        cut.WaitForAssertion(() => AdminBilling.LastQuery!.From.ShouldBe(new DateOnly(2026, 6, 1)));
        cut.Find("[data-testid=clear-filters]").Click();

        cut.WaitForAssertion(() => AdminBilling.LastQuery!.Status.ShouldBeNull());
        AdminBilling.LastQuery!.From.ShouldBeNull();
        cut.FindAll("[data-testid=clear-filters]").ShouldBeEmpty();
    }
}

public class AdminBillingOrderDetailPageTests : BillingPageTestBase
{
    private IRenderedComponent<MudDialogProvider> _providers = null!;

    private IRenderedComponent<AdminBillingOrderDetailPage> RenderReady(params string[] permissions)
    {
        SignInAsBillingStaff([WebPermissions.BillingOrdersRead, .. permissions]);
        _providers = Providers();
        var cut = Render<AdminBillingOrderDetailPage>(p => p.Add(x => x.Id, BillingSample.OrderId));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=order-card]").Count.ShouldBe(1));
        return cut;
    }

    private void Type(string testId, string value) =>
        _providers.FindAll($"input[data-testid={testId}],textarea[data-testid={testId}],[data-testid={testId}] input,[data-testid={testId}] textarea").First().Input(value);

    [Fact]
    public void Shows_the_payment_partner_details_the_breakdown_and_the_timeline()
    {
        var cut = RenderReady();

        var card = cut.Find("[data-testid=order-card]").TextContent;
        card.ShouldContain("stripe");
        card.ShouldContain("pi_123");
        card.ShouldContain("Acme Corp");
        cut.Find("[data-testid=price-subtotal]").TextContent.ShouldBe("$49.00");
        cut.Find("[data-testid=events]").TextContent.ShouldContain("Payment confirmed");
        cut.Find("[data-testid=no-refunds]").TextContent.ShouldContain("Nothing has been refunded");
    }

    [Fact]
    public void Refunds_are_listed_with_the_credits_taken_back()
    {
        AdminBilling.Order = () => Ok.Of(BillingSample.AdminOrder(OrderStatuses.PartiallyRefunded, [new RefundDto(Guid.NewGuid(), 1000, 850, "Duplicate purchase", BillingSample.Now)]));

        var cut = RenderReady();

        var row = cut.Find("[data-testid=refund-row]");
        row.TextContent.ShouldContain("$10.00");
        row.TextContent.ShouldContain("850");
        row.TextContent.ShouldContain("Duplicate purchase");
    }

    [Fact]
    public void Loading_empty_error_and_not_found_states()
    {
        SignInAsBillingStaff(WebPermissions.BillingOrdersRead);
        var gate = new Gate<ApiResult<AdminOrderDto>>();
        AdminBilling.Order = () => gate.Task;
        var cut = Render<AdminBillingOrderDetailPage>(p => p.Add(x => x.Id, BillingSample.OrderId));
        cut.FindAll("[data-testid=skeleton-card]").Count.ShouldBe(1);
        gate.Release(ApiResult<AdminOrderDto>.Fail("NOT_FOUND", "We couldn't find that. It may have been removed.", "corr-aod", 404));

        cut.WaitForAssertion(() => cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-aod"));
    }

    [Fact]
    public void People_without_the_refund_permission_see_no_refund_or_reconcile_buttons()
    {
        var cut = RenderReady();

        cut.FindAll("[data-testid=refund]").ShouldBeEmpty();
        cut.FindAll("[data-testid=reconcile]").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(OrderStatuses.Pending)]
    [InlineData(OrderStatuses.Failed)]
    [InlineData(OrderStatuses.Refunded)]
    public void Only_paid_orders_with_something_left_can_be_refunded(string status)
    {
        AdminBilling.Order = () => Ok.Of(BillingSample.AdminOrder(status));

        var cut = RenderReady(WebPermissions.BillingRefund);

        cut.FindAll("[data-testid=refund]").ShouldBeEmpty();
        cut.FindAll("[data-testid=reconcile]").Count.ShouldBe(1);
    }

    [Fact]
    public void A_fully_refunded_total_hides_the_refund_button_even_if_the_status_lags()
    {
        AdminBilling.Order = () => Ok.Of(BillingSample.AdminOrder(OrderStatuses.PartiallyRefunded, [new RefundDto(Guid.NewGuid(), 5880, 5000, "all", BillingSample.Now)]));

        var cut = RenderReady(WebPermissions.BillingRefund);

        cut.FindAll("[data-testid=refund]").ShouldBeEmpty();
    }

    [Fact]
    public void A_refund_needs_a_reason_and_the_typed_order_number_before_it_can_be_confirmed()
    {
        var snacks = Render<MudSnackbarProvider>();
        var cut = RenderReady(WebPermissions.BillingRefund);

        cut.Find("[data-testid=refund]").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=refund-ok]").Count.ShouldBe(1));
        _providers.Find("[data-testid=refund-phrase]").TextContent.ShouldBe("NV-2026-001042");
        _providers.Find("[data-testid=refund-remaining]").TextContent.ShouldBe("$58.80");
        _providers.Find("[data-testid=refund-ok]").HasAttribute("disabled").ShouldBeTrue();

        Type("refund-reason", "Customer paid twice");
        _providers.Find("[data-testid=refund-ok]").HasAttribute("disabled").ShouldBeTrue("the order number is still missing");
        Type("refund-confirm-input", "NV-2026-0010");
        _providers.Find("[data-testid=refund-ok]").HasAttribute("disabled").ShouldBeTrue("a near miss does not count");
        Type("refund-confirm-input", "NV-2026-001042");
        _providers.Find("[data-testid=refund-ok]").HasAttribute("disabled").ShouldBeFalse();
        _providers.Find("[data-testid=refund-ok]").Click();

        cut.WaitForAssertion(() => AdminBilling.LastRefund.ShouldNotBeNull());
        (AdminBilling.LastRefund!.AmountMinor, AdminBilling.LastRefund.Reason).ShouldBe((null, "Customer paid twice"));
        snacks.WaitForAssertion(() => snacks.Markup.ShouldContain("rest of the order was refunded"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=refund-row]").Count.ShouldBe(1), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void A_partial_refund_is_typed_in_major_units_and_sent_in_minor_units()
    {
        var cut = RenderReady(WebPermissions.BillingRefund);
        cut.Find("[data-testid=refund]").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=refund-ok]").Count.ShouldBe(1));

        Type("refund-amount", "12.50");
        Type("refund-reason", "Goodwill");
        Type("refund-confirm-input", "NV-2026-001042");
        _providers.Find("[data-testid=refund-ok]").Click();

        cut.WaitForAssertion(() => AdminBilling.LastRefund.ShouldNotBeNull());
        AdminBilling.LastRefund!.AmountMinor.ShouldBe(1250);
    }

    [Theory]
    [InlineData("58.81", "more than what is left")]
    [InlineData("0", "more than zero")]
    [InlineData("5.555", "at most 2 decimals")]
    [InlineData("ten", "at most 2 decimals")]
    public void An_amount_that_cannot_be_refunded_blocks_the_button_and_says_why(string amount, string message)
    {
        var cut = RenderReady(WebPermissions.BillingRefund);
        cut.Find("[data-testid=refund]").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=refund-ok]").Count.ShouldBe(1));

        Type("refund-amount", amount);
        Type("refund-reason", "x");
        Type("refund-confirm-input", "NV-2026-001042");

        _providers.Markup.ShouldContain(message);
        _providers.Find("[data-testid=refund-ok]").HasAttribute("disabled").ShouldBeTrue();
        AdminBilling.LastRefund.ShouldBeNull();
    }

    [Fact]
    public void Money_already_refunded_lowers_what_is_left()
    {
        AdminBilling.Order = () => Ok.Of(BillingSample.AdminOrder(OrderStatuses.PartiallyRefunded, [new RefundDto(Guid.NewGuid(), 2000, 1000, "r", BillingSample.Now)]));
        var cut = RenderReady(WebPermissions.BillingRefund);

        cut.Find("[data-testid=refund]").Click();

        _providers.WaitForAssertion(() => _providers.Find("[data-testid=refund-remaining]").TextContent.ShouldBe("$38.80"));
        _providers.Find("[data-testid=refund-summary]").TextContent.ShouldContain("$20.00");
    }

    [Fact]
    public void Cancelling_the_refund_changes_nothing()
    {
        var cut = RenderReady(WebPermissions.BillingRefund);
        cut.Find("[data-testid=refund]").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=refund-cancel]").Count.ShouldBe(1));

        _providers.Find("[data-testid=refund-cancel]").Click();

        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=refund-ok]").Count.ShouldBe(0));
        AdminBilling.Calls.ShouldNotContain("refund");
    }

    [Fact]
    public void A_refused_refund_shows_the_error_with_its_reference_and_keeps_the_page()
    {
        AdminBilling.Refund = _ => Ok.Fail<AdminOrderDto>("CONFLICT", "The payment partner refused the refund.", "corr-refund", 409);
        var snacks = Render<MudSnackbarProvider>();
        var cut = RenderReady(WebPermissions.BillingRefund);
        cut.Find("[data-testid=refund]").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=refund-ok]").Count.ShouldBe(1));
        Type("refund-reason", "x");
        Type("refund-confirm-input", "NV-2026-001042");

        _providers.Find("[data-testid=refund-ok]").Click();

        snacks.WaitForAssertion(() => snacks.Markup.ShouldContain("corr-refund"));
        cut.FindAll("[data-testid=refund-row]").ShouldBeEmpty();
        cut.FindAll("[data-testid=refund]").Count.ShouldBe(1);
    }

    [Fact]
    public void Reconcile_asks_the_partner_and_refreshes_the_order()
    {
        var snacks = Render<MudSnackbarProvider>();
        var cut = RenderReady(WebPermissions.BillingRefund);

        cut.Find("[data-testid=reconcile]").Click();

        cut.WaitForAssertion(() => AdminBilling.Calls.ShouldContain("reconcile"));
        snacks.WaitForAssertion(() => snacks.Markup.ShouldContain("up to date"));
    }

    [Fact]
    public void The_refund_dialog_uses_the_order_reference_when_no_invoice_was_issued()
    {
        AdminBilling.Order = () => Ok.Of(BillingSample.AdminOrder() with { InvoiceNumber = null });
        var cut = RenderReady(WebPermissions.BillingRefund);

        cut.Find("[data-testid=refund]").Click();

        _providers.WaitForAssertion(() => _providers.Find("[data-testid=refund-phrase]").TextContent.ShouldBe(BillingSample.OrderId.ToString("N")[..8].ToUpperInvariant()));
    }
}

public class BillingComponentTests : UiTestBase
{
    [Fact]
    public void The_price_lines_add_up_from_the_servers_numbers_and_name_the_tax()
    {
        var cut = Render<PriceBreakdown>(p => p.Add(x => x.SubtotalMinor, 4900).Add(x => x.TaxMinor, 980).Add(x => x.TotalMinor, 5880).Add(x => x.Currency, "USD")
            .Add(x => x.TaxLabel, "GST").Add(x => x.TaxPercent, 18.5m));

        cut.Find("[data-testid=price-tax]").TextContent.ShouldBe("$9.80");
        cut.Markup.ShouldContain("GST (18.5%)");
        cut.Find("[data-testid=price-total]").TextContent.ShouldBe("$58.80");
    }

    [Fact]
    public void Without_tax_the_line_says_none()
    {
        var cut = Render<PriceBreakdown>(p => p.Add(x => x.SubtotalMinor, 1500).Add(x => x.TaxMinor, 0).Add(x => x.TotalMinor, 1500).Add(x => x.Currency, "JPY"));

        cut.Find("[data-testid=price-tax]").TextContent.ShouldBe("None");
        cut.Find("[data-testid=price-total]").TextContent.ShouldBe("¥1,500");
    }

    [Fact]
    public void Best_value_needs_two_comparable_packs_and_a_real_difference()
    {
        var starter = BillingSample.Starter();
        var growth = BillingSample.Growth();

        PurchasePackCard.BestValueId([starter]).ShouldBeNull();
        PurchasePackCard.BestValueId([starter, growth]).ShouldBe(growth.Id);
        PurchasePackCard.BestValueId([growth, starter]).ShouldBe(growth.Id);
        PurchasePackCard.BestValueId([starter, starter with { Id = Guid.NewGuid() }]).ShouldBeNull("identical rates: nothing stands out");
        PurchasePackCard.BestValueId([starter, growth with { Currency = "EUR" }]).ShouldBeNull("different currencies cannot be compared");
    }

    [Fact]
    public void A_pack_card_never_renders_api_text_as_markup()
    {
        var evil = BillingSample.Starter() with { Name = "<img src=x onerror=alert(1)>", Description = "<script>alert(2)</script>", Highlights = ["<b>bold</b>"] };

        var cut = Render<PurchasePackCard>(p => p.Add(x => x.Pack, evil).Add(x => x.CanBuy, true));

        cut.FindAll("img").ShouldBeEmpty();
        cut.FindAll("script").ShouldBeEmpty();
        cut.FindAll("b").ShouldBeEmpty();
        cut.Markup.ShouldContain("&lt;img");
    }

    [Fact]
    public void The_buy_button_is_labelled_for_screen_readers_with_pack_and_price()
    {
        var cut = Render<PurchasePackCard>(p => p.Add(x => x.Pack, BillingSample.Starter()).Add(x => x.CanBuy, true));

        cut.Find("[data-testid=buy-pack]").GetAttribute("aria-label").ShouldBe("Buy Starter pack for $58.80");
    }

    [Theory]
    [InlineData("Pending", "Waiting for payment")]
    [InlineData("Paid", "Paid")]
    [InlineData("PartiallyRefunded", "Partly refunded")]
    [InlineData("Refunded", "Refunded")]
    [InlineData("Failed", "Payment failed")]
    [InlineData("Cancelled", "Cancelled")]
    [InlineData("Expired", "Expired")]
    public void Every_order_status_has_a_plain_label_and_never_relies_on_colour_alone(string status, string label)
    {
        var cut = Render<StatusChip>(p => p.Add(x => x.Kind, StatusKind.Order).Add(x => x.Value, status));

        cut.Find("[data-status]").GetAttribute("data-status").ShouldBe(label);
        cut.Markup.ShouldContain("<svg", Case.Insensitive);
    }
}
