using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Billing;
using NexaVerify.Domain.Billing;
using NexaVerify.Infrastructure.Background;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>
/// The anonymous provider webhook endpoint with the real Stripe and Razorpay adapters (provider HTTP calls are replaced by a stub):
/// signature before anything else, idempotency through the event ledger, amount checks, concurrency, and the nightly reconcile job.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class BillingWebhookTests : BillingTestBase
{
    private const string RazorpayWebhookSecret = "rzp_webhook_integration";

    public BillingWebhookTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    protected override Dictionary<string, string> Settings => BillingSettings("Stripe");

    protected override void ConfigureServices(IServiceCollection services)
    {
        // The provider adapters talk to a stub instead of the internet.
        services.AddHttpClient<NexaVerify.Infrastructure.Billing.StripeProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => new FakeStripe());
    }

    public sealed class FakeStripe : HttpMessageHandler
    {
        public static volatile string SessionState = "open";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                "/v1/checkout/sessions" => "{\"id\":\"cs_test_" + Guid.NewGuid().ToString("N") + "\",\"url\":\"https://checkout.stripe.test/pay/cs_test_fake\"}",
                _ when path.EndsWith("/expire", StringComparison.Ordinal) => "{\"id\":\"cs_test_fake\"}",
                _ when path.StartsWith("/v1/checkout/sessions/", StringComparison.Ordinal) =>
                    SessionState == "paid"
                        ? "{\"payment_status\":\"paid\",\"status\":\"complete\",\"payment_intent\":\"pi_" + path[^32..] + "\",\"amount_total\":118000,\"currency\":\"inr\"}"
                        : "{\"payment_status\":\"unpaid\",\"status\":\"open\"}",
                "/v1/refunds" => "{\"id\":\"re_fake\",\"status\":\"succeeded\"}",
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static string StripeBody(string eventId, Guid orderId, long amount = 118_000, string currency = "inr", string type = "checkout.session.completed", string paymentStatus = "paid") =>
        $"{{\"id\":\"{eventId}\",\"type\":\"{type}\",\"data\":{{\"object\":{{\"id\":\"cs_test_fake\",\"client_reference_id\":\"{orderId}\",\"payment_status\":\"{paymentStatus}\",\"payment_intent\":\"pi_fake\",\"amount_total\":{amount},\"currency\":\"{currency}\"}}}}}}";

    private static string StripeHeader(string body, long? timestamp = null, string secret = StripeWebhookSecret)
    {
        var t = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}")));
        return $"t={t},v1={sig}";
    }

    private Task<HttpResponseMessage> PostWebhookAsync(string provider, string body, string? headerName = null, string? headerValue = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/billing/webhooks/{provider}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (headerName is not null)
        {
            request.Headers.Add(headerName, headerValue);
        }

        return App.Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> StripeAsync(string body, string? header = null) =>
        PostWebhookAsync("stripe", body, "Stripe-Signature", header ?? StripeHeader(body));

    private async Task<(Tenant Tenant, CheckoutResponseDto Checkout)> OpenOrderAsync(string code)
    {
        var tenant = await NewTenantAsync(code);
        await SaveProfileAsync(tenant);
        var pack = await NewPackAsync(credits: 500);
        var checkout = await StartCheckoutAsync(tenant, pack.Id);
        return (tenant, checkout);
    }

    [Fact]
    public async Task A_signed_stripe_webhook_grants_the_credits_and_a_redelivery_changes_nothing()
    {
        var (tenant, checkout) = await OpenOrderAsync("WH1");
        checkout.CheckoutUrl.ShouldBe("https://checkout.stripe.test/pay/cs_test_fake");
        var body = StripeBody("evt_1", checkout.OrderId);

        var first = await StripeAsync(body);
        var again = await StripeAsync(body);

        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        var order = await GetOrderAsync(tenant, checkout.OrderId);
        order.Status.ShouldBe("Paid");
        (await LicenseCountAsync(tenant.ClientId, "Top-up")).ShouldBe(1);
        (await LicenseAsync(tenant.ClientId, order.LicenseId!.Value)).TotalCredits.ShouldBe(500);
        var events = (await EventsAsync()).Where(e => e.EventId == "evt_1").ToList();
        events.Count.ShouldBe(1);
        events[0].Outcome.ShouldBe("Granted");
        events[0].ProcessedAt.ShouldNotBeNull();
        (await AdminOrderAsync(checkout.OrderId)).ProviderPaymentId.ShouldBe("pi_fake");
    }

    [Fact]
    public async Task Parallel_deliveries_of_the_same_and_of_different_events_grant_exactly_once()
    {
        var (tenant, checkout) = await OpenOrderAsync("WH2");

        var calls = Enumerable.Range(0, 6).Select(_ => StripeAsync(StripeBody("evt_same", checkout.OrderId)))
            .Concat(Enumerable.Range(0, 6).Select(i => StripeAsync(StripeBody("evt_other_" + i, checkout.OrderId))))
            .Concat(Enumerable.Range(0, 2).Select(_ => App.PostAsync($"/api/v1/admin/billing/orders/{checkout.OrderId}/reconcile", null, Platform.AccessToken)));
        var responses = await Task.WhenAll(calls);

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        (await LicenseCountAsync(tenant.ClientId, "Top-up")).ShouldBe(1);
        var order = await GetOrderAsync(tenant, checkout.OrderId);
        (await LedgerAsync(tenant.ClientId, order.LicenseId!.Value)).Count(t => t.Type == Domain.Licensing.LedgerEntryType.Grant).ShouldBe(1);
        App.Emails.Sent.Count(m => m.Subject.Contains(order.InvoiceNumber!, StringComparison.Ordinal)).ShouldBe(1);
        (await App.WithDbAsync(db => db.PaymentEvents.CountAsync(e => e.Outcome == "Granted"))).ShouldBe(1);
    }

    [Fact]
    public async Task A_bad_signature_is_a_400_and_leaves_no_trace()
    {
        var (tenant, checkout) = await OpenOrderAsync("WH3");
        var body = StripeBody("evt_bad", checkout.OrderId);

        var tampered = await StripeAsync(body.Replace("118000", "1", StringComparison.Ordinal), StripeHeader(body));
        var wrongSecret = await StripeAsync(body, StripeHeader(body, secret: "whsec_other"));
        var stale = await StripeAsync(body, StripeHeader(body, DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds()));
        var missing = await PostWebhookAsync("stripe", body);
        var notJson = await StripeAsync("{{{", StripeHeader("{{{"));

        tampered.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        wrongSecret.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        stale.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        notJson.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await GetOrderAsync(tenant, checkout.OrderId)).Status.ShouldBe("Pending");
        (await EventsAsync()).ShouldBeEmpty();
        (await LicenseCountAsync(tenant.ClientId, "Top-up")).ShouldBe(0);
    }

    [Fact]
    public async Task Only_the_configured_provider_has_a_webhook_and_the_body_is_capped()
    {
        var (tenant, checkout) = await OpenOrderAsync("WH4");

        (await PostWebhookAsync("razorpay", "{}", "X-Razorpay-Signature", "00")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostWebhookAsync("simulated", "{}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostWebhookAsync("paypal", "{}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var huge = new string('x', 70_000);
        (await StripeAsync(huge, StripeHeader(huge))).StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await PostWebhookAsync("stripe", string.Empty, "Stripe-Signature", "t=1,v1=00")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await GetOrderAsync(tenant, checkout.OrderId)).Status.ShouldBe("Pending");
    }

    [Fact]
    public async Task A_signed_event_with_the_wrong_amount_or_currency_is_flagged_and_grants_nothing()
    {
        var (tenant, checkout) = await OpenOrderAsync("WH5");

        (await StripeAsync(StripeBody("evt_low", checkout.OrderId, amount: 100))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StripeAsync(StripeBody("evt_cur", checkout.OrderId, currency: "usd"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StripeAsync(StripeBody("evt_ghost", Guid.NewGuid()))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await GetOrderAsync(tenant, checkout.OrderId)).Status.ShouldBe("Pending");
        (await LicenseCountAsync(tenant.ClientId, "Top-up")).ShouldBe(0);
        var outcomes = (await EventsAsync()).ToDictionary(e => e.EventId, e => e.Outcome);
        outcomes["evt_low"].ShouldBe("AnomalyAmountMismatch");
        outcomes["evt_cur"].ShouldBe("AnomalyAmountMismatch");
        outcomes["evt_ghost"].ShouldBe("AnomalyOrderNotFound");
    }

    [Fact]
    public async Task An_unpaid_completed_session_is_ignored_and_expiry_and_failure_events_update_the_order()
    {
        var (tenant, checkout) = await OpenOrderAsync("WH6");

        (await StripeAsync(StripeBody("evt_unpaid", checkout.OrderId, paymentStatus: "unpaid"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetOrderAsync(tenant, checkout.OrderId)).Status.ShouldBe("Pending");

        (await StripeAsync(StripeBody("evt_exp", checkout.OrderId, type: "checkout.session.expired", paymentStatus: "unpaid"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetOrderAsync(tenant, checkout.OrderId)).Status.ShouldBe("Expired");

        // an expired page that was paid after all (late delivery) is still honoured
        (await StripeAsync(StripeBody("evt_late", checkout.OrderId))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetOrderAsync(tenant, checkout.OrderId)).Status.ShouldBe("Paid");
    }

    [Fact]
    public async Task A_provider_refund_we_did_not_make_is_flagged_for_a_human()
    {
        var (_, checkout) = await OpenOrderAsync("WH7");
        var body = $"{{\"id\":\"evt_ref\",\"type\":\"refund.created\",\"data\":{{\"object\":{{\"id\":\"re_external\",\"payment_intent\":\"pi_fake\",\"amount\":500,\"currency\":\"inr\",\"metadata\":{{\"orderId\":\"{checkout.OrderId}\"}}}}}}}}";

        (await StripeAsync(body)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await EventsAsync()).Single(e => e.EventId == "evt_ref").Outcome.ShouldBe("AnomalyExternalRefund");
    }

    [Fact]
    public async Task A_late_webhook_is_made_up_for_by_asking_the_provider_when_the_order_is_polled()
    {
        var (tenant, checkout) = await OpenOrderAsync("WH8");
        FakeStripe.SessionState = "paid";
        try
        {
            var order = await GetOrderAsync(tenant, checkout.OrderId);

            order.Status.ShouldBe("Paid");
            order.LicenseId.ShouldNotBeNull();
            (await LicenseCountAsync(tenant.ClientId, "Top-up")).ShouldBe(1);
        }
        finally
        {
            FakeStripe.SessionState = "open";
        }
    }

    [Fact]
    public async Task The_maintenance_job_reconciles_old_pending_orders_and_expires_stale_ones()
    {
        var (tenant, paidLater) = await OpenOrderAsync("WH9");
        var pack = await NewPackAsync("Second");
        var stale = await StartCheckoutAsync(tenant, pack.Id);
        var fresh = await StartCheckoutAsync(tenant, pack.Id);
        await AgeOrderAsync(paidLater.OrderId, TimeSpan.FromMinutes(20));
        await AgeOrderAsync(stale.OrderId, TimeSpan.FromHours(25));
        var processor = App.Factory.Services.GetRequiredService<BillingMaintenanceProcessor>();

        FakeStripe.SessionState = "paid";
        int changed;
        try
        {
            changed = await processor.RunOnceAsync(default);
        }
        finally
        {
            FakeStripe.SessionState = "open";
        }

        // both aged orders look "paid" to the stub provider; the fresh one (under 15 minutes) is left alone
        changed.ShouldBe(2);
        (await GetOrderAsync(tenant, paidLater.OrderId)).Status.ShouldBe("Paid");
        (await GetOrderAsync(tenant, fresh.OrderId)).Status.ShouldBe("Pending");

        // with nothing paid, the 25 hour old one is simply closed
        var stale2 = await StartCheckoutAsync(tenant, pack.Id);
        await AgeOrderAsync(stale2.OrderId, TimeSpan.FromHours(30));
        (await processor.RunOnceAsync(default)).ShouldBe(1);
        (await GetOrderAsync(tenant, stale2.OrderId)).Status.ShouldBe("Expired");
        (await AuditActionsAsync(tenant.ClientId)).ShouldContain("billing.order_expired");
        (await processor.RunOnceAsync(default)).ShouldBe(0);
    }

    [Fact]
    public async Task Refunds_go_through_the_stripe_adapter()
    {
        var (tenant, checkout) = await OpenOrderAsync("WH10");
        (await StripeAsync(StripeBody("evt_pay", checkout.OrderId))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var refund = await App.PostAsync($"/api/v1/admin/billing/orders/{checkout.OrderId}/refund", new RefundOrderRequest(null, "test"), Platform.AccessToken);

        refund.StatusCode.ShouldBe(HttpStatusCode.OK, await refund.Content.ReadAsStringAsync());
        var detail = await refund.Content.ReadFromJsonAsync<AdminOrderDto>(AuthApp.Json);
        detail!.Refunds.Single().ProviderRefundId.ShouldBe("re_fake");
        (await GetOrderAsync(tenant, checkout.OrderId)).Status.ShouldBe("Refunded");
    }
}

[Collection(SqlServerCollection.Name)]
public class BillingRazorpayWebhookTests : BillingTestBase
{
    public BillingRazorpayWebhookTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    protected override Dictionary<string, string> Settings => BillingSettings("Razorpay");

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddHttpClient<NexaVerify.Infrastructure.Billing.RazorpayProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => new FakeRazorpay());
    }

    private sealed class FakeRazorpay : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"plink_fake\",\"short_url\":\"https://rzp.test/i/fake\"}", Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public async Task A_signed_razorpay_webhook_grants_credits_once_even_when_delivered_twice()
    {
        var tenant = await NewTenantAsync("RZ1");
        await SaveProfileAsync(tenant);
        var pack = await NewPackAsync(credits: 250);
        var checkout = await StartCheckoutAsync(tenant, pack.Id);
        checkout.CheckoutUrl.ShouldBe("https://rzp.test/i/fake");
        var body = $"{{\"event\":\"payment_link.paid\",\"payload\":{{\"payment_link\":{{\"entity\":{{\"id\":\"plink_fake\",\"reference_id\":\"{checkout.OrderId}\",\"amount_paid\":118000,\"currency\":\"INR\"}}}},\"payment\":{{\"entity\":{{\"id\":\"pay_fake\",\"amount\":118000,\"currency\":\"INR\",\"status\":\"captured\"}}}}}}}}";
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("rzp_webhook_integration"), Encoding.UTF8.GetBytes(body)));

        async Task<HttpResponseMessage> Send(string sig)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/webhooks/razorpay") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Razorpay-Signature", sig);
            request.Headers.Add("X-Razorpay-Event-Id", "rzp_evt_1");
            return await App.Client.SendAsync(request);
        }

        (await Send(new string('0', 64))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Send(signature)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Send(signature)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await GetOrderAsync(tenant, checkout.OrderId)).Status.ShouldBe("Paid");
        (await LicenseCountAsync(tenant.ClientId, "Top-up")).ShouldBe(1);
        (await EventsAsync()).Count(e => e.Outcome == "Granted").ShouldBe(1);
    }
}

/// <summary>Where the simulated provider and its dev-only endpoint may exist.</summary>
public class BillingEnvironmentGuardTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void The_simulated_provider_refuses_to_start_outside_development_and_testing(string environment)
    {
        var settings = new Dictionary<string, string>(BillingTestBase.BillingSettings("Simulated"))
        {
            ["Hosting:RedirectToHttps"] = "false",
            ["AllowedHosts"] = "api.example.com",
            ["Webhooks:AllowUnsafeTargets"] = "false",
        };
        using var factory = new ApiFactory { Environment = environment, Settings = settings, UseTestAuth = false };

        Should.Throw<InvalidOperationException>(() => factory.CreateClient()).Message.ShouldContain("Simulated");
    }

    [Fact]
    public async Task The_dev_simulation_route_does_not_exist_outside_development_and_testing()
    {
        await using var factory = new ApiFactory
        {
            Environment = "Staging",
            UseTestAuth = false,
            Settings = new Dictionary<string, string> { ["Hosting:RedirectToHttps"] = "false", ["AllowedHosts"] = "api.example.com", ["Billing:Enabled"] = "false" },
        };
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Host = "api.example.com";

        // not mapped at all: the deny-by-default policy answers before any routing-specific response
        (await client.PostAsJsonAsync($"/api/v1/dev/billing/simulate/{Guid.NewGuid()}", new SimulatePaymentRequest("success"))).StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Billing_off_by_default_answers_404_and_an_empty_public_list()
    {
        await using var factory = new ApiFactory { UseTestAuth = false };
        var client = factory.CreateClient();

        var packs = await client.GetAsync("/api/v1/public/packs");
        packs.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await packs.Content.ReadAsStringAsync()).ShouldBe("[]");
        (await client.PostAsync("/api/v1/billing/webhooks/stripe", new StringContent("{}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
