using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Billing;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Dashboards;
using NexaVerify.Domain.Api;
using NexaVerify.Domain.Billing;
using NexaVerify.Domain.Licensing;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>
/// The whole purchase journey against the real API and a real SQL Server with the simulated payment provider: packs, checkout, payment,
/// credits, invoice, refund, and every way it must NOT work (other tenants, other roles, tampering, repeats).
/// </summary>
[Collection(SqlServerCollection.Name)]
public class BillingEndToEndTests : BillingTestBase
{
    private readonly WebhookReceiver _receiver = new();

    public BillingEndToEndTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _receiver.DisposeAsync();
    }

    // ---- the happy path ----

    [Fact]
    public async Task A_client_buys_credits_and_receives_a_license_an_invoice_and_a_receipt()
    {
        var tenant = await NewTenantAsync("E2E1");
        (await App.PostAsync("/api/v1/client/webhooks", new CreateWebhookRequest("billing", _receiver.Url, [WebhookEvents.LicenseToppedUp]), tenant.Token))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var pack = await NewPackAsync("Growth", credits: 1_500, days: 45, priceMinor: 100_000);

        // the pack list shows the price before tax, the tax and the total
        var packs = await App.GetAsync("/api/v1/client/billing/packs", tenant.Token);
        packs.StatusCode.ShouldBe(HttpStatusCode.OK);
        var listed = (await packs.Content.ReadFromJsonAsync<List<CreditPackDto>>(AuthApp.Json))!.Single(p => p.Id == pack.Id);
        listed.PriceMinor.ShouldBe(100_000);
        listed.TaxPercent.ShouldBe(18m);
        listed.TaxLabel.ShouldBe("GST");
        listed.TaxMinor.ShouldBe(18_000);
        listed.TotalMinor.ShouldBe(118_000);
        listed.Credits.ShouldBe(1_500);
        listed.Highlights.ShouldBe(["Highlight one", "Highlight two"]);

        // without billing details there is no checkout
        var blocked = await CheckoutAsync(tenant, pack.Id);
        blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(blocked)).ShouldBe("BILLING_PROFILE_INCOMPLETE");
        await SaveProfileAsync(tenant);

        var before = DateTime.UtcNow;
        var checkout = await StartCheckoutAsync(tenant, pack.Id);
        checkout.CheckoutUrl.ShouldBe($"https://localhost:7200/dev/pay/{checkout.OrderId}");
        checkout.ExpiresAt.ShouldBeGreaterThan(before);

        var pending = await GetOrderAsync(tenant, checkout.OrderId);
        pending.Status.ShouldBe("Pending");
        pending.LicenseId.ShouldBeNull();
        pending.InvoiceNumber.ShouldBeNull();
        pending.TotalMinor.ShouldBe(118_000);
        (await App.GetAsync($"/api/v1/client/billing/orders/{checkout.OrderId}/invoice", tenant.Token)).StatusCode.ShouldBe(HttpStatusCode.Conflict); // not paid yet

        var paid = await PayAsync(tenant, checkout.OrderId);

        paid.Status.ShouldBe("Paid");
        paid.PaidAt.ShouldNotBeNull();
        paid.LicenseId.ShouldNotBeNull();
        paid.InvoiceNumber.ShouldBe($"INV-{DateTime.UtcNow.Year}-000001");

        // the license: right credits, right expiry, the ledger Grant names the order
        var license = await ClientLicenseAsync(tenant, paid.LicenseId!.Value);
        license.Name.ShouldBe("Top-up: Growth");
        license.TotalCredits.ShouldBe(1_500);
        license.RemainingCredits.ShouldBe(1_500);
        license.EffectiveStatus.ShouldBe("Active");
        (license.ExpiresAt - license.StartsAt).TotalDays.ShouldBe(45, 0.01);
        license.StartsAt.ShouldBeInRange(before.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        var ledger = await LedgerAsync(tenant.ClientId, paid.LicenseId.Value);
        ledger.Count.ShouldBe(1);
        ledger[0].Type.ShouldBe(LedgerEntryType.Grant);
        ledger[0].Credits.ShouldBe(1_500);
        ledger[0].Reason.ShouldNotBeNull().ShouldContain(checkout.OrderId.ToString());

        // the invoice: tax breakdown, seller, buyer
        var invoice = await App.GetAsync($"/api/v1/client/billing/orders/{checkout.OrderId}/invoice", tenant.Token);
        invoice.StatusCode.ShouldBe(HttpStatusCode.OK);
        invoice.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        invoice.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("style-src 'unsafe-inline'");
        var html = await invoice.Content.ReadAsStringAsync();
        html.ShouldContain(paid.InvoiceNumber);
        html.ShouldContain("Seller Pvt Ltd");
        html.ShouldContain("GSTIN: 27AAAAA0000A1Z5");
        html.ShouldContain("Acme Pvt Ltd");
        html.ShouldContain("29ABCDE1234F1Z5");
        html.ShouldContain("INR 1,000.00");
        html.ShouldContain("GST 18%");
        html.ShouldContain("INR 180.00");
        html.ShouldContain("INR 1,180.00");

        // audit, receipt, notification and webhook event
        var actions = await AuditActionsAsync(tenant.ClientId);
        actions.ShouldContain("billing.profile_updated");
        actions.ShouldContain("billing.checkout_created");
        actions.ShouldContain("billing.payment_succeeded");
        actions.ShouldContain("license.created");
        var receipt = App.Emails.Sent.Single(m => m.Subject.Contains(paid.InvoiceNumber, StringComparison.Ordinal));
        receipt.To.ShouldBe("billing@acme.test");
        receipt.Body.ShouldContain("1,500 credits");
        receipt.Body.ShouldContain("INR 1,180.00");
        receipt.Body.ShouldContain($"/billing/orders/{checkout.OrderId}");
        var feed = await App.GetAsync("/api/v1/client/notifications", tenant.Token);
        (await feed.Content.ReadFromJsonAsync<NotificationFeedDto>(AuthApp.Json))!.Page.Items.ShouldContain(n => n.Title == "Credits added" && n.Message.Contains("1500", StringComparison.Ordinal));
        var delivery = (await DeliveriesAsync(tenant.ClientId)).Single(d => d.EventType == WebhookEvents.LicenseToppedUp);
        delivery.PayloadJson.ShouldContain(checkout.OrderId.ToString());
        delivery.PayloadJson.ShouldContain(paid.LicenseId.Value.ToString());

        // listing, filtering and export
        var list = await App.GetAsync("/api/v1/client/billing/orders?status=Paid", tenant.Token);
        var page = (await list.Content.ReadFromJsonAsync<PagedResult<OrderListItemDto>>(AuthApp.Json))!;
        page.TotalCount.ShouldBe(1);
        page.Items[0].InvoiceNumber.ShouldBe(paid.InvoiceNumber);
        (await App.GetAsync("/api/v1/client/billing/orders?status=Pending", tenant.Token)).Content.ReadFromJsonAsync<PagedResult<OrderListItemDto>>(AuthApp.Json).Result!.TotalCount.ShouldBe(0);
        var csv = await App.GetAsync("/api/v1/client/billing/orders/export.csv", tenant.Token);
        csv.StatusCode.ShouldBe(HttpStatusCode.OK);
        csv.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var text = await csv.Content.ReadAsStringAsync();
        text.ShouldContain(paid.InvoiceNumber);
        text.ShouldContain("118000");
    }

    [Fact]
    public async Task Invoice_numbers_follow_each_other_without_gaps_across_clients()
    {
        var a = await NewTenantAsync("INV1");
        var b = await NewTenantAsync("INV2");
        await SaveProfileAsync(a);
        await SaveProfileAsync(b);
        var pack = await NewPackAsync();

        var first = await BuyAsync(a, pack);
        var second = await BuyAsync(b, pack);
        var third = await BuyAsync(a, pack);

        var numbers = new[] { first, second, third }.Select(o => o.InvoiceNumber!).ToList();
        numbers.Select(n => int.Parse(n[^6..])).ShouldBe([1, 2, 3]);
        numbers.ShouldAllBe(n => n.StartsWith($"INV-{DateTime.UtcNow.Year}-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_or_expired_payment_changes_the_status_and_grants_nothing_but_a_later_real_payment_still_counts()
    {
        var tenant = await NewTenantAsync("FAIL1");
        await SaveProfileAsync(tenant);
        var pack = await NewPackAsync();

        var failedOrder = await StartCheckoutAsync(tenant, pack.Id);
        (await SimulateAsync(tenant, failedOrder.OrderId, "failure")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var failed = await GetOrderAsync(tenant, failedOrder.OrderId);
        failed.Status.ShouldBe("Failed");
        failed.LicenseId.ShouldBeNull();

        var expiredOrder = await StartCheckoutAsync(tenant, pack.Id);
        (await SimulateAsync(tenant, expiredOrder.OrderId, "expired")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetOrderAsync(tenant, expiredOrder.OrderId)).Status.ShouldBe("Expired");
        (await LicenseCountAsync(tenant.ClientId, "Top-up")).ShouldBe(0);

        // the customer retried on the provider's page after the failure and paid: that is real money, so it is honoured
        var paid = await PayAsync(tenant, failedOrder.OrderId);
        paid.Status.ShouldBe("Paid");
        paid.LicenseId.ShouldNotBeNull();
        (await LicenseCountAsync(tenant.ClientId, "Top-up")).ShouldBe(1);
    }

    [Fact]
    public async Task A_cancelled_order_stays_cancelled_and_a_late_payment_is_flagged_not_granted()
    {
        var tenant = await NewTenantAsync("CANC1");
        await SaveProfileAsync(tenant);
        var pack = await NewPackAsync();
        var checkout = await StartCheckoutAsync(tenant, pack.Id);

        var cancel = await App.PostAsync($"/api/v1/client/billing/orders/{checkout.OrderId}/cancel", null, tenant.Token);
        cancel.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cancel.Content.ReadFromJsonAsync<OrderDto>(AuthApp.Json))!.Status.ShouldBe("Cancelled");
        (await App.PostAsync($"/api/v1/client/billing/orders/{checkout.OrderId}/cancel", null, tenant.Token)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await SimulateAsync(tenant, checkout.OrderId, "success")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await GetOrderAsync(tenant, checkout.OrderId)).Status.ShouldBe("Cancelled");
        (await LicenseCountAsync(tenant.ClientId, "Top-up")).ShouldBe(0);
        (await EventsAsync()).ShouldContain(e => e.OrderId == checkout.OrderId && e.Outcome == "AnomalyNotPayable");
    }

    // ---- idempotency, tampering ----

    [Fact]
    public async Task The_same_idempotency_key_returns_the_same_checkout_and_never_a_second_order()
    {
        var tenant = await NewTenantAsync("IDEM1");
        await SaveProfileAsync(tenant);
        var pack = await NewPackAsync();
        var other = await NewPackAsync("Other");

        var first = await StartCheckoutAsync(tenant, pack.Id, "buy-001");
        var again = await StartCheckoutAsync(tenant, pack.Id, "buy-001");

        again.OrderId.ShouldBe(first.OrderId);
        again.CheckoutUrl.ShouldBe(first.CheckoutUrl);
        (await App.WithTenantDbAsync(tenant.ClientId, db => db.PaymentOrders.CountAsync())).ShouldBe(1);

        var different = await CheckoutAsync(tenant, other.Id, "buy-001");
        different.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await PayAsync(tenant, first.OrderId);
        (await CheckoutAsync(tenant, pack.Id, "buy-001")).StatusCode.ShouldBe(HttpStatusCode.Conflict); // finished: a new purchase needs a new key
        (await StartCheckoutAsync(tenant, pack.Id, "buy-002")).OrderId.ShouldNotBe(first.OrderId);
    }

    [Fact]
    public async Task Two_clients_may_use_the_same_idempotency_key_without_seeing_each_other()
    {
        var a = await NewTenantAsync("IDEM2");
        var b = await NewTenantAsync("IDEM3");
        await SaveProfileAsync(a);
        await SaveProfileAsync(b);
        var pack = await NewPackAsync();

        var first = await StartCheckoutAsync(a, pack.Id, "shared-key");
        var second = await StartCheckoutAsync(b, pack.Id, "shared-key");

        second.OrderId.ShouldNotBe(first.OrderId);
    }

    [Fact]
    public async Task The_price_comes_from_the_database_whatever_the_request_says_and_later_edits_do_not_touch_an_open_order()
    {
        var tenant = await NewTenantAsync("TAMP1");
        await SaveProfileAsync(tenant);
        var pack = await NewPackAsync(priceMinor: 100_000);

        // a client that tries to smuggle an amount in: the field simply does not exist
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/client/billing/checkout")
        {
            Content = JsonContent.Create(new { packId = pack.Id, amountMinor = 1, totalMinor = 1, priceMinor = 1, currency = "USD", clientId = Guid.NewGuid() }),
        };
        var response = await App.SendAsync(request, tenant.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var checkout = (await response.Content.ReadFromJsonAsync<CheckoutResponseDto>(AuthApp.Json))!;

        var order = await GetOrderAsync(tenant, checkout.OrderId);
        order.TotalMinor.ShouldBe(118_000);
        order.Currency.ShouldBe("INR");

        // the staff raise the price afterwards: the order keeps what the customer was shown, and the payment is judged against that
        (await App.PutAsync($"/api/v1/admin/billing/packs/{pack.Id}",
            new UpdateCreditPackRequest("Growth", null, 1_000, 90, 500_000, "INR", null, 0, true, true), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetOrderAsync(tenant, checkout.OrderId)).TotalMinor.ShouldBe(118_000);
        (await PayAsync(tenant, checkout.OrderId)).Status.ShouldBe("Paid");
    }

    [Fact]
    public async Task A_payment_for_the_wrong_amount_is_recorded_as_an_anomaly_and_grants_nothing()
    {
        var tenant = await NewTenantAsync("TAMP2");
        await SaveProfileAsync(tenant);
        var pack = await NewPackAsync();
        var checkout = await StartCheckoutAsync(tenant, pack.Id);

        // the processor is what every provider event ends up in; feed it events a hostile or buggy provider could send
        await using var scope = App.Factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<NexaVerify.Application.Billing.IPaymentEventProcessor>();
        using (scope.ServiceProvider.GetRequiredService<NexaVerify.Application.Abstractions.ITenantScope>().BeginPlatform("test"))
        {
            NexaVerify.Application.Billing.NormalisedPaymentEvent Event(string id, long? amount, string? currency, string provider = "simulated") =>
                new(provider, id, NexaVerify.Application.Billing.PaymentEventType.PaymentSucceeded, checkout.OrderId, "pay_1", amount, currency);

            (await processor.ProcessAsync(Event("e1", 100, "INR"), default)).Value.ShouldBe("AnomalyAmountMismatch");
            (await processor.ProcessAsync(Event("e2", 118_000, "USD"), default)).Value.ShouldBe("AnomalyAmountMismatch");
            (await processor.ProcessAsync(Event("e3", null, "INR"), default)).Value.ShouldBe("AnomalyMissingData");
            (await processor.ProcessAsync(Event("e4", 118_000, "INR", provider: "stripe"), default)).Value.ShouldBe("AnomalyProviderMismatch");
        }

        (await GetOrderAsync(tenant, checkout.OrderId)).Status.ShouldBe("Pending");
        (await LicenseCountAsync(tenant.ClientId, "Top-up")).ShouldBe(0);
        (await AuditActionsAsync(tenant.ClientId)).ShouldContain("billing.payment_anomaly");
    }

    [Fact]
    public async Task A_payment_event_for_an_unknown_order_is_recorded_and_ignored()
    {
        await using var scope = App.Factory.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<NexaVerify.Application.Billing.IPaymentEventProcessor>();
        using var platform = scope.ServiceProvider.GetRequiredService<NexaVerify.Application.Abstractions.ITenantScope>().BeginPlatform("test");

        var result = await processor.ProcessAsync(
            new("simulated", "ghost-1", NexaVerify.Application.Billing.PaymentEventType.PaymentSucceeded, Guid.NewGuid(), "pay_x", 100, "INR"), default);

        result.Value.ShouldBe("AnomalyOrderNotFound");
    }

    // ---- authorisation and tenant isolation ----

    [Fact]
    public async Task Another_client_can_not_read_pay_cancel_or_invoice_an_order()
    {
        var a = await NewTenantAsync("ISO1");
        var b = await NewTenantAsync("ISO2");
        await SaveProfileAsync(a);
        var pack = await NewPackAsync();
        var order = await BuyAsync(a, pack);
        var open = await StartCheckoutAsync(a, pack.Id);

        (await App.GetAsync($"/api/v1/client/billing/orders/{order.Id}", b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await App.GetAsync($"/api/v1/client/billing/orders/{order.Id}/invoice", b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await App.PostAsync($"/api/v1/client/billing/orders/{open.OrderId}/cancel", null, b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SimulateAsync(b, open.OrderId, "success")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetOrderAsync(a, open.OrderId)).Status.ShouldBe("Pending");

        var list = await App.GetAsync("/api/v1/client/billing/orders", b.Token);
        (await list.Content.ReadFromJsonAsync<PagedResult<OrderListItemDto>>(AuthApp.Json))!.TotalCount.ShouldBe(0);
        var csv = await (await App.GetAsync("/api/v1/client/billing/orders/export.csv", b.Token)).Content.ReadAsStringAsync();
        csv.ShouldNotContain(order.InvoiceNumber!);

        // billing profiles are per client too
        var profile = await App.GetAsync("/api/v1/client/billing/profile", b.Token);
        (await profile.Content.ReadFromJsonAsync<BillingProfileDto>(AuthApp.Json))!.LegalName.ShouldBeEmpty();
    }

    [Fact]
    public async Task Order_rows_are_invisible_across_tenants_at_the_database_too()
    {
        var a = await NewTenantAsync("RLS1");
        var b = await NewTenantAsync("RLS2");
        await SaveProfileAsync(a);
        var pack = await NewPackAsync();
        await BuyAsync(a, pack);

        (await App.WithTenantDbAsync(b.ClientId, db => db.PaymentOrders.CountAsync())).ShouldBe(0);
        (await App.WithTenantDbAsync(b.ClientId, db => db.BillingProfiles.CountAsync())).ShouldBe(0);
        (await App.WithTenantDbAsync(a.ClientId, db => db.PaymentOrders.CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task Each_endpoint_demands_the_right_credential()
    {
        var tenant = await NewTenantAsync("AUTHZ1");
        await SaveProfileAsync(tenant);
        var pack = await NewPackAsync();
        var order = await StartCheckoutAsync(tenant, pack.Id);
        var plainUser = await NewClientUserTokenAsync(tenant.ClientId, "user@authz1.test");

        string[] clientGets =
        [
            "/api/v1/client/billing/config", "/api/v1/client/billing/packs", "/api/v1/client/billing/orders", $"/api/v1/client/billing/orders/{order.OrderId}",
            $"/api/v1/client/billing/orders/{order.OrderId}/invoice", "/api/v1/client/billing/orders/export.csv", "/api/v1/client/billing/profile",
        ];
        foreach (var path in clientGets)
        {
            (await App.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, path);
            (await App.GetAsync(path, plainUser)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a ClientUser has no billing permission: " + path);
            (await App.GetAsync(path, Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "staff are not a client: " + path);
        }

        (await App.PostAsync("/api/v1/client/billing/checkout", new CheckoutRequest(pack.Id, null))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CheckoutAsync(new Tenant(tenant.ClientId, plainUser, "x"), pack.Id)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await App.PostAsync("/api/v1/client/billing/checkout", new CheckoutRequest(pack.Id, null), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await App.PutAsync("/api/v1/client/billing/profile", Profile(), plainUser)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await App.PostAsync($"/api/v1/client/billing/orders/{order.OrderId}/cancel", null, plainUser)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        string[] adminGets =
        [
            "/api/v1/admin/billing/config", "/api/v1/admin/billing/packs", $"/api/v1/admin/billing/packs/{pack.Id}", "/api/v1/admin/billing/orders",
            $"/api/v1/admin/billing/orders/{order.OrderId}",
        ];
        foreach (var path in adminGets)
        {
            (await App.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, path);
            (await App.GetAsync(path, tenant.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a client admin is not staff: " + path);
        }

        (await App.PostAsync("/api/v1/admin/billing/packs", new CreateCreditPackRequest("x", null, 1, 1, 1, "INR", null, 0, true, true), tenant.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await App.PutAsync($"/api/v1/admin/billing/packs/{pack.Id}", new UpdateCreditPackRequest("x", null, 1, 1, 1, "INR", null, 0, true, true), tenant.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await App.DeleteAsync($"/api/v1/admin/billing/packs/{pack.Id}", tenant.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await App.PostAsync($"/api/v1/admin/billing/orders/{order.OrderId}/refund", new RefundOrderRequest(null, "x"), tenant.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await App.PostAsync($"/api/v1/admin/billing/orders/{order.OrderId}/reconcile", null, tenant.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await App.PostAsync($"/api/v1/admin/billing/orders/{order.OrderId}/refund", new RefundOrderRequest(null, "x"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_api_key_can_never_use_billing()
    {
        var tenant = await NewTenantAsync("KEY1");
        var pack = await NewPackAsync();
        var created = await App.PostAsync("/api/v1/client/api-keys", new CreateApiKeyRequest("integration", ["faces.read"], null, null, null), tenant.Token);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var raw = (await created.Content.ReadFromJsonAsync<CreatedApiKeyDto>(AuthApp.Json))!.RawKey;

        // billing permissions are not assignable to a key...
        var withBilling = await App.PostAsync("/api/v1/client/api-keys", new CreateApiKeyRequest("greedy", ["billing.manage"], null, null, null), tenant.Token);
        withBilling.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // ...and a key that has other scopes is refused at every billing endpoint
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, "/api/v1/client/billing/packs"), (HttpMethod.Get, "/api/v1/client/billing/orders"),
                     (HttpMethod.Post, "/api/v1/client/billing/checkout"), (HttpMethod.Get, "/api/v1/client/billing/profile"),
                 })
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Api-Key", raw);
            if (method == HttpMethod.Post)
            {
                request.Content = JsonContent.Create(new CheckoutRequest(pack.Id, null));
            }

            (await App.Client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }
    }

    [Fact]
    public async Task The_provider_mode_is_shown_to_staff_only_and_secrets_never_appear_in_any_response()
    {
        var tenant = await NewTenantAsync("CFG1");

        var staff = await (await App.GetAsync("/api/v1/admin/billing/config", Platform.AccessToken)).Content.ReadAsStringAsync();
        var client = await (await App.GetAsync("/api/v1/client/billing/config", tenant.Token)).Content.ReadAsStringAsync();

        var admin = JsonSerializer.Deserialize<AdminBillingConfigDto>(staff, AuthApp.Json)!;
        admin.Enabled.ShouldBeTrue();
        admin.Provider.ShouldBe("Simulated");
        admin.Mode.ShouldBe("simulated");
        admin.TaxPercent.ShouldBe(18m);
        var config = JsonSerializer.Deserialize<BillingConfigDto>(client, AuthApp.Json)!;
        config.Enabled.ShouldBeTrue();
        config.Currencies.ShouldBe(["INR", "USD"]);
        client.ShouldNotContain("simulated", Case.Insensitive);
        client.ShouldNotContain("provider", Case.Insensitive);
        foreach (var body in new[] { staff, client })
        {
            body.ShouldNotContain("sk_test_integration");
            body.ShouldNotContain(StripeWebhookSecret);
            body.ShouldNotContain("rzp_secret_integration");
            body.ShouldNotContain("rzp_webhook_integration");
        }
    }

    // ---- validation ----

    [Fact]
    public async Task Bad_input_is_a_400_and_an_unknown_or_foreign_currency_pack_is_a_404()
    {
        var tenant = await NewTenantAsync("VAL1");
        await SaveProfileAsync(tenant);
        var euro = await NewPackAsync("Euro pack", currency: "EUR"); // not in Billing:AllowedCurrencies
        var off = await NewPackAsync("Switched off", isActive: false);

        (await CheckoutAsync(tenant, Guid.Empty)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CheckoutAsync(tenant, Guid.NewGuid())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CheckoutAsync(tenant, euro.Id)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CheckoutAsync(tenant, off.Id)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CheckoutAsync(tenant, off.Id, "has spaces")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var listed = await (await App.GetAsync("/api/v1/client/billing/packs", tenant.Token)).Content.ReadFromJsonAsync<List<CreditPackDto>>(AuthApp.Json);
        listed!.Select(p => p.Id).ShouldNotContain(euro.Id);
        listed.Select(p => p.Id).ShouldNotContain(off.Id);

        (await App.PutAsync("/api/v1/client/billing/profile", Profile() with { Country = "India" }, tenant.Token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await App.PutAsync("/api/v1/client/billing/profile", Profile() with { BillingEmail = "nope" }, tenant.Token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await App.PutAsync("/api/v1/client/billing/profile", Profile() with { LegalName = "" }, tenant.Token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await App.GetAsync("/api/v1/client/billing/orders?status=Bogus", tenant.Token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await App.GetAsync($"/api/v1/client/billing/orders/{Guid.NewGuid()}", tenant.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await App.PostAsync("/api/v1/admin/billing/packs", new CreateCreditPackRequest("", null, 0, 0, 0, "rupee", null, 0, true, true), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await App.PostAsync($"/api/v1/dev/billing/simulate/{Guid.NewGuid()}", new SimulatePaymentRequest("explode"), tenant.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_profile_is_stored_normalised_and_its_changes_are_audited_without_the_tax_id()
    {
        var tenant = await NewTenantAsync("PROF1");

        var empty = await (await App.GetAsync("/api/v1/client/billing/profile", tenant.Token)).Content.ReadFromJsonAsync<BillingProfileDto>(AuthApp.Json);
        empty!.IsComplete.ShouldBeFalse();

        await SaveProfileAsync(tenant, Profile() with { Country = "in", TaxId = "29abcde 1234-f1z5" });
        var saved = await (await App.GetAsync("/api/v1/client/billing/profile", tenant.Token)).Content.ReadFromJsonAsync<BillingProfileDto>(AuthApp.Json);
        saved!.Country.ShouldBe("IN");
        saved.TaxId.ShouldBe("29ABCDE1234F1Z5");
        saved.IsComplete.ShouldBeTrue();

        await SaveProfileAsync(tenant, Profile() with { TaxId = null, LegalName = "Acme Renamed" }); // update path
        (await App.WithTenantDbAsync(tenant.ClientId, db => db.BillingProfiles.CountAsync())).ShouldBe(1);
        var audit = await App.WithDbAsync(db => db.AuditLogs.Where(a => a.ClientId == tenant.ClientId && a.Action == "billing.profile_updated").Select(a => a.NewValuesJson).ToListAsync());
        audit.Count.ShouldBe(2);
        audit.ShouldAllBe(json => !json!.Contains("29ABCDE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_invoice_uses_the_profile_as_it_was_at_checkout_not_as_it_is_now()
    {
        var tenant = await NewTenantAsync("SNAP1");
        await SaveProfileAsync(tenant);
        var pack = await NewPackAsync();
        var checkout = await StartCheckoutAsync(tenant, pack.Id);
        await SaveProfileAsync(tenant, Profile() with { LegalName = "Completely Different Name Ltd" });

        await PayAsync(tenant, checkout.OrderId);

        var html = await (await App.GetAsync($"/api/v1/client/billing/orders/{checkout.OrderId}/invoice", tenant.Token)).Content.ReadAsStringAsync();
        html.ShouldContain("Acme Pvt Ltd");
        html.ShouldNotContain("Completely Different");
    }

    [Fact]
    public async Task A_html_injection_in_the_profile_can_not_reach_the_invoice_page()
    {
        var tenant = await NewTenantAsync("XSS1");
        await SaveProfileAsync(tenant, Profile() with { LegalName = "<script>alert(1)</script>Evil" });
        var pack = await NewPackAsync();
        var order = await BuyAsync(tenant, pack);

        var html = await (await App.GetAsync($"/api/v1/client/billing/orders/{order.Id}/invoice", tenant.Token)).Content.ReadAsStringAsync();

        html.ShouldNotContain("<script>");
        html.ShouldContain("&lt;script&gt;");
    }

    // ---- packs: admin catalogue and public listing ----

    [Fact]
    public async Task Staff_manage_the_pack_catalogue_and_a_pack_with_orders_can_only_be_switched_off()
    {
        var tenant = await NewTenantAsync("PACK1");
        await SaveProfileAsync(tenant);
        var unused = await NewPackAsync("Never bought");
        var bought = await NewPackAsync("Bought");
        await BuyAsync(tenant, bought);

        var update = await App.PutAsync($"/api/v1/admin/billing/packs/{unused.Id}",
            new UpdateCreditPackRequest("Renamed", "d", 2_000, 30, 55_000, "usd", ["x"], 5, true, false), Platform.AccessToken);
        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = (await update.Content.ReadFromJsonAsync<AdminCreditPackDto>(AuthApp.Json))!;
        updated.Name.ShouldBe("Renamed");
        updated.Currency.ShouldBe("USD");
        updated.IsPublic.ShouldBeFalse();

        (await App.DeleteAsync($"/api/v1/admin/billing/packs/{bought.Id}", Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await App.DeleteAsync($"/api/v1/admin/billing/packs/{unused.Id}", Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await App.GetAsync($"/api/v1/admin/billing/packs/{unused.Id}", Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await App.PutAsync($"/api/v1/admin/billing/packs/{Guid.NewGuid()}",
            new UpdateCreditPackRequest("x", null, 1, 1, 1, "INR", null, 0, true, true), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var actions = await App.WithDbAsync(db => db.AuditLogs.Where(a => a.Action.StartsWith("billing.pack_")).Select(a => a.Action).ToListAsync());
        actions.ShouldContain("billing.pack_created");
        actions.ShouldContain("billing.pack_updated");
        actions.ShouldContain("billing.pack_deleted");
    }

    [Fact]
    public async Task The_public_pack_list_is_anonymous_cacheable_and_shows_only_public_active_packs_in_allowed_currencies()
    {
        var shown = await NewPackAsync("Public shown", priceMinor: 49_900, order: -5);
        await NewPackAsync("Private", isPublic: false);
        await NewPackAsync("Retired", isActive: false);
        await NewPackAsync("Euro", currency: "EUR");

        var response = await App.GetAsync("/api/v1/public/packs");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.MaxAge.ShouldBe(TimeSpan.FromSeconds(60));
        response.Headers.CacheControl.Public.ShouldBeTrue();
        var packs = (await response.Content.ReadFromJsonAsync<List<PublicPackDto>>(AuthApp.Json))!;
        packs.Select(p => p.Name).ShouldBe(["Public shown"], "private, inactive and non-allowed-currency packs are never public");
        var pack = packs.Single();
        pack.Id.ShouldBe(shown.Id);
        pack.PriceMinor.ShouldBe(49_900);
        pack.TotalMinor.ShouldBe(58_882);
        pack.TaxLabel.ShouldBe("GST");
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("isActive");
    }

    // ---- refunds ----

    [Fact]
    public async Task A_full_refund_takes_back_the_unused_credits_and_never_makes_the_balance_negative()
    {
        var tenant = await NewTenantAsync("REF1");
        await SaveProfileAsync(tenant);
        var pack = await NewPackAsync(credits: 1_000, priceMinor: 100_000);
        (await App.PostAsync("/api/v1/client/webhooks", new CreateWebhookRequest("billing", _receiver.Url, [WebhookEvents.LicenseCreditsRevoked]), tenant.Token))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var order = await BuyAsync(tenant, pack);
        await SetConsumedAsync(order.LicenseId!.Value, 300); // the customer already used 300 credits

        var refund = await App.PostAsync($"/api/v1/admin/billing/orders/{order.Id}/refund", new RefundOrderRequest(null, "Customer asked for their money back"), Platform.AccessToken);

        refund.StatusCode.ShouldBe(HttpStatusCode.OK, await refund.Content.ReadAsStringAsync());
        var detail = (await refund.Content.ReadFromJsonAsync<AdminOrderDto>(AuthApp.Json))!;
        detail.Order.Status.ShouldBe("Refunded");
        detail.Order.RefundedMinor.ShouldBe(118_000);
        detail.CreditsRevoked.ShouldBe(700, "only the unused credits can be taken back");
        detail.Refunds.Single().AmountMinor.ShouldBe(118_000);
        detail.Refunds.Single().Status.ShouldBe("Succeeded");
        detail.Refunds.Single().CreditsRevoked.ShouldBe(700);
        detail.Refunds.Single().ProviderRefundId.ShouldNotBeNullOrEmpty();

        var license = await LicenseAsync(tenant.ClientId, order.LicenseId.Value);
        license.TotalCredits.ShouldBe(300);
        license.ConsumedCredits.ShouldBe(300);
        license.Remaining.ShouldBe(0);
        var ledger = await LedgerAsync(tenant.ClientId, order.LicenseId.Value);
        ledger.Last().Type.ShouldBe(LedgerEntryType.Adjustment);
        ledger.Last().Credits.ShouldBe(-700);
        ledger.Last().BalanceAfter.ShouldBe(0);
        ledger.Last().Reason.ShouldNotBeNull().ShouldContain(order.InvoiceNumber!);

        // audited, announced, explained to the customer
        (await AuditActionsAsync(tenant.ClientId)).ShouldContain("billing.refund");
        (await DeliveriesAsync(tenant.ClientId)).ShouldContain(d => d.EventType == WebhookEvents.LicenseCreditsRevoked && d.PayloadJson.Contains("700", StringComparison.Ordinal));
        App.Emails.Sent.ShouldContain(m => m.Subject.StartsWith("Refund issued", StringComparison.Ordinal) && m.Body.Contains("700", StringComparison.Ordinal));
        var clientView = await GetOrderAsync(tenant, order.Id);
        clientView.Status.ShouldBe("Refunded");
        var html = await (await App.GetAsync($"/api/v1/client/billing/orders/{order.Id}/invoice", tenant.Token)).Content.ReadAsStringAsync();
        html.ShouldContain("Refunded");

        // nothing left to refund
        (await App.PostAsync($"/api/v1/admin/billing/orders/{order.Id}/refund", new RefundOrderRequest(null, "again"), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Partial_refunds_revoke_credits_in_proportion_and_add_up_exactly()
    {
        var tenant = await NewTenantAsync("REF2");
        await SaveProfileAsync(tenant);
        var pack = await NewPackAsync(credits: 1_000, priceMinor: 100_000); // total 118 000
        var order = await BuyAsync(tenant, pack);

        var first = await RefundAsync(order.Id, 59_000, "half");
        first.Order.Status.ShouldBe("PartiallyRefunded");
        first.Order.RefundedMinor.ShouldBe(59_000);
        first.CreditsRevoked.ShouldBe(500);

        var second = await RefundAsync(order.Id, 29_500, "a quarter");
        second.CreditsRevoked.ShouldBe(750);
        second.Order.Status.ShouldBe("PartiallyRefunded");

        var last = await RefundAsync(order.Id, null, "the rest");
        last.Order.Status.ShouldBe("Refunded");
        last.Order.RefundedMinor.ShouldBe(118_000);
        last.CreditsRevoked.ShouldBe(1_000);
        last.Refunds.Count.ShouldBe(3);
        last.Refunds.Sum(r => r.CreditsRevoked).ShouldBe(1_000);
        (await LicenseAsync(tenant.ClientId, order.LicenseId!.Value)).TotalCredits.ShouldBe(0);
    }

    [Fact]
    public async Task A_refund_must_fit_what_is_left_and_only_a_paid_order_can_be_refunded()
    {
        var tenant = await NewTenantAsync("REF3");
        await SaveProfileAsync(tenant);
        var pack = await NewPackAsync();
        var unpaid = await StartCheckoutAsync(tenant, pack.Id);
        var paid = await BuyAsync(tenant, pack);

        (await App.PostAsync($"/api/v1/admin/billing/orders/{unpaid.OrderId}/refund", new RefundOrderRequest(null, "x"), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await App.PostAsync($"/api/v1/admin/billing/orders/{paid.Id}/refund", new RefundOrderRequest(118_001, "too much"), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await App.PostAsync($"/api/v1/admin/billing/orders/{paid.Id}/refund", new RefundOrderRequest(0, "zero"), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await App.PostAsync($"/api/v1/admin/billing/orders/{paid.Id}/refund", new RefundOrderRequest(100, ""), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await App.PostAsync($"/api/v1/admin/billing/orders/{Guid.NewGuid()}/refund", new RefundOrderRequest(100, "x"), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetOrderAsync(tenant, paid.Id)).RefundedMinor.ShouldBe(0);
    }

    [Fact]
    public async Task Two_simultaneous_refunds_never_pay_out_more_than_the_order_total()
    {
        var tenant = await NewTenantAsync("REF4");
        await SaveProfileAsync(tenant);
        var pack = await NewPackAsync(credits: 1_000);
        var order = await BuyAsync(tenant, pack);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            App.PostAsync($"/api/v1/admin/billing/orders/{order.Id}/refund", new RefundOrderRequest(null, "parallel " + i), Platform.AccessToken)));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(3);
        var detail = await AdminOrderAsync(order.Id);
        detail.Order.RefundedMinor.ShouldBe(118_000);
        detail.Refunds.Count(r => r.Status == "Succeeded").ShouldBe(1);
    }

    [Fact]
    public async Task A_refund_of_a_license_that_has_already_expired_still_refunds_the_money_and_revokes_nothing()
    {
        var tenant = await NewTenantAsync("REF5");
        await SaveProfileAsync(tenant);
        var pack = await NewPackAsync(credits: 100);
        var order = await BuyAsync(tenant, pack);
        await App.WithDbAsync(async db =>
        {
            await db.Licenses.Where(l => l.Id == order.LicenseId).ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, LicenseStatus.Expired).SetProperty(l => l.TotalCredits, 0).SetProperty(l => l.ConsumedCredits, 0));
            return true;
        });

        var detail = await RefundAsync(order.Id, null, "expired unused");

        detail.Order.Status.ShouldBe("Refunded");
        detail.CreditsRevoked.ShouldBe(0);
    }

    // ---- admin views ----

    [Fact]
    public async Task Staff_see_every_clients_orders_with_filters_and_a_detail_with_provider_ids()
    {
        var a = await NewTenantAsync("ADM1");
        var b = await NewTenantAsync("ADM2");
        await SaveProfileAsync(a);
        await SaveProfileAsync(b);
        var pack = await NewPackAsync();
        var paidA = await BuyAsync(a, pack);
        var openB = await StartCheckoutAsync(b, pack.Id);

        var all = await ListAdminAsync("");
        all.Items.Select(o => o.Id).ShouldContain(paidA.Id);
        all.Items.Select(o => o.Id).ShouldContain(openB.OrderId);
        (await ListAdminAsync($"?clientId={a.ClientId}")).Items.ShouldAllBe(o => o.ClientId == a.ClientId);
        (await ListAdminAsync("?status=Pending")).Items.ShouldAllBe(o => o.Status == "Pending");
        (await ListAdminAsync("?status=Paid&clientId=" + b.ClientId)).TotalCount.ShouldBe(0);
        (await ListAdminAsync($"?from={DateTime.UtcNow.AddDays(1):yyyy-MM-dd}")).TotalCount.ShouldBe(0);
        (await ListAdminAsync($"?to={DateTime.UtcNow.AddDays(1):yyyy-MM-dd}&clientId={a.ClientId}")).TotalCount.ShouldBeGreaterThan(0);
        (await App.GetAsync("/api/v1/admin/billing/orders?status=Bogus", Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var detail = await AdminOrderAsync(paidA.Id);
        detail.ClientName.ShouldContain("ADM1");
        detail.Provider.ShouldBe("simulated");
        detail.ProviderPaymentId.ShouldNotBeNullOrEmpty();
        detail.ProviderSessionId.ShouldNotBeNullOrEmpty();
        (await App.GetAsync($"/api/v1/admin/billing/orders/{Guid.NewGuid()}", Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<PagedResult<AdminOrderListItemDto>> ListAdminAsync(string query)
    {
        var response = await App.GetAsync("/api/v1/admin/billing/orders" + query, Platform.AccessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PagedResult<AdminOrderListItemDto>>(AuthApp.Json))!;
    }

    private async Task<AdminOrderDto> RefundAsync(Guid orderId, long? amount, string reason)
    {
        var response = await App.PostAsync($"/api/v1/admin/billing/orders/{orderId}/refund", new RefundOrderRequest(amount, reason), Platform.AccessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AdminOrderDto>(AuthApp.Json))!;
    }

    // ---- concurrency ----

    [Fact]
    public async Task Many_simultaneous_payment_confirmations_grant_the_credits_exactly_once()
    {
        var tenant = await NewTenantAsync("CONC1");
        await SaveProfileAsync(tenant);
        var pack = await NewPackAsync(credits: 777);
        var checkout = await StartCheckoutAsync(tenant, pack.Id);

        // twelve "providers" confirm the same payment at once (each simulate call is a distinct event), plus two staff reconciles
        var calls = Enumerable.Range(0, 12).Select(_ => SimulateAsync(tenant, checkout.OrderId, "success"))
            .Concat(Enumerable.Range(0, 2).Select(_ => App.PostAsync($"/api/v1/admin/billing/orders/{checkout.OrderId}/reconcile", null, Platform.AccessToken)));
        var responses = await Task.WhenAll(calls);

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        var order = await GetOrderAsync(tenant, checkout.OrderId);
        order.Status.ShouldBe("Paid");
        (await LicenseCountAsync(tenant.ClientId, "Top-up")).ShouldBe(1);
        (await LedgerAsync(tenant.ClientId, order.LicenseId!.Value)).Count(t => t.Type == LedgerEntryType.Grant).ShouldBe(1);
        (await LicenseAsync(tenant.ClientId, order.LicenseId.Value)).TotalCredits.ShouldBe(777);
        App.Emails.Sent.Count(m => m.Subject.Contains(order.InvoiceNumber!, StringComparison.Ordinal)).ShouldBe(1);
        (await AuditActionsAsync(tenant.ClientId)).Count(a => a == "billing.payment_succeeded").ShouldBe(1);
        (await App.WithDbAsync(db => db.LicenseAlerts.CountAsync(a => a.SubjectId == order.Id))).ShouldBe(1);
    }

    [Fact]
    public async Task Simultaneous_payments_of_different_orders_get_distinct_gapless_invoice_numbers()
    {
        var tenant = await NewTenantAsync("CONC2");
        await SaveProfileAsync(tenant);
        var pack = await NewPackAsync();
        var checkouts = new List<CheckoutResponseDto>();
        for (var i = 0; i < 6; i++)
        {
            checkouts.Add(await StartCheckoutAsync(tenant, pack.Id));
        }

        await Task.WhenAll(checkouts.Select(c => SimulateAsync(tenant, c.OrderId, "success")));

        var numbers = new List<int>();
        foreach (var c in checkouts)
        {
            numbers.Add(int.Parse((await GetOrderAsync(tenant, c.OrderId)).InvoiceNumber![^6..]));
        }

        numbers.Order().ShouldBe(Enumerable.Range(1, 6), "gapless and unique, whatever the interleaving");
    }

    // ---- the simulation itself ----

    [Fact]
    public async Task The_simulator_demands_a_signed_in_client_with_billing_rights_and_a_valid_outcome()
    {
        var tenant = await NewTenantAsync("SIM1");
        await SaveProfileAsync(tenant);
        var pack = await NewPackAsync();
        var checkout = await StartCheckoutAsync(tenant, pack.Id);

        (await App.PostAsync($"/api/v1/dev/billing/simulate/{checkout.OrderId}", new SimulatePaymentRequest("success"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await App.PostAsync($"/api/v1/dev/billing/simulate/{checkout.OrderId}", new SimulatePaymentRequest("success"), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SimulateAsync(tenant, checkout.OrderId, "maybe")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await GetOrderAsync(tenant, checkout.OrderId)).Status.ShouldBe("Pending");
    }

    [Fact]
    public async Task A_redirect_back_from_the_provider_proves_nothing_the_order_stays_pending()
    {
        var tenant = await NewTenantAsync("REDIR1");
        await SaveProfileAsync(tenant);
        var pack = await NewPackAsync();
        var checkout = await StartCheckoutAsync(tenant, pack.Id);

        // the customer's browser comes back to the success page (or someone calls the API as if it had): polling does not change anything
        for (var i = 0; i < 3; i++)
        {
            (await GetOrderAsync(tenant, checkout.OrderId)).Status.ShouldBe("Pending");
        }

        (await LicenseCountAsync(tenant.ClientId, "Top-up")).ShouldBe(0);
    }
}
