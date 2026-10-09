using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NexaVerify.Application.Billing;
using NexaVerify.Application.Public;
using NexaVerify.Infrastructure.Billing;

namespace NexaVerify.Infrastructure.UnitTests.Billing;

/// <summary>
/// The payment provider adapters, with no network. Signature tests use fixed, externally computed vectors (HMAC-SHA256 as documented by
/// Stripe and Razorpay) plus variations: tampered body, wrong secret, stale or future timestamp, missing header, replayed event.
/// NOT verified against the live providers: the request/response shapes follow their public documentation.
/// </summary>
public class PaymentProviderTests
{
    private const string OrderId = "0192f4c0-7a11-7c3e-9a3a-5b6f7c8d9e01";
    private const string StripeSecret = "whsec_known_vector_secret";
    private const string RazorpaySecret = "rzp_known_vector_secret";

    // Recorded-style bodies and the signatures computed for them outside this code base (python hmac/hashlib).
    private const string StripeBody =
        "{\"id\":\"evt_1PabcDEFghiJKLmn\",\"object\":\"event\",\"api_version\":\"2024-06-20\",\"created\":1700000000,\"type\":\"checkout.session.completed\",\"data\":{\"object\":{\"id\":\"cs_test_a1B2c3\",\"object\":\"checkout.session\",\"amount_total\":118000,\"currency\":\"inr\",\"client_reference_id\":\"0192f4c0-7a11-7c3e-9a3a-5b6f7c8d9e01\",\"metadata\":{\"orderId\":\"0192f4c0-7a11-7c3e-9a3a-5b6f7c8d9e01\"},\"payment_intent\":\"pi_3Pabc\",\"payment_status\":\"paid\",\"status\":\"complete\"}}}";

    private const string StripeVectorSignature = "5f400ce9d895c1afa17bc9251a477ec091fb4d9aea5e67c075e59138353c8d51"; // HMAC("1700000000." + body)

    private const string RazorpayBody =
        "{\"entity\":\"event\",\"account_id\":\"acc_Ab12\",\"event\":\"payment_link.paid\",\"contains\":[\"payment_link\",\"order\",\"payment\"],\"payload\":{\"payment_link\":{\"entity\":{\"id\":\"plink_Pabc123\",\"reference_id\":\"0192f4c0-7a11-7c3e-9a3a-5b6f7c8d9e01\",\"amount\":118000,\"amount_paid\":118000,\"currency\":\"INR\",\"status\":\"paid\",\"notes\":{\"orderId\":\"0192f4c0-7a11-7c3e-9a3a-5b6f7c8d9e01\"}}},\"order\":{\"entity\":{\"id\":\"order_Pxyz\",\"amount\":118000,\"status\":\"paid\"}},\"payment\":{\"entity\":{\"id\":\"pay_Pdef456\",\"amount\":118000,\"currency\":\"INR\",\"status\":\"captured\",\"order_id\":\"order_Pxyz\"}}},\"created_at\":1700000000}";

    private const string RazorpayVectorSignature = "99f7731b5f634adbe0f147bf3a5a1c81dd9b61ae397e8bf6651b11f095b7b9d8";

    private static readonly DateTimeOffset VectorTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static BillingOptions Options(string provider = "Stripe") => new()
    {
        Enabled = true,
        Provider = provider,
        Stripe = { SecretKey = "sk_test_key", WebhookSecret = StripeSecret, ApiBaseUrl = "https://api.stripe.test" },
        Razorpay = { KeyId = "rzp_test_id", KeySecret = "rzp_test_secret", WebhookSecret = RazorpaySecret, ApiBaseUrl = "https://api.razorpay.test" },
    };

    private static StripeProvider Stripe(StubHandler? handler = null, FakeTimeProvider? time = null) =>
        new(Http(handler ?? new StubHandler(), "https://api.stripe.test/"), Microsoft.Extensions.Options.Options.Create(Options()), time ?? new FakeTimeProvider(VectorTime));

    private static RazorpayProvider Razorpay(StubHandler? handler = null, FakeTimeProvider? time = null) =>
        new(Http(handler ?? new StubHandler(), "https://api.razorpay.test/"), Microsoft.Extensions.Options.Options.Create(Options("Razorpay")), time ?? new FakeTimeProvider(VectorTime));

    private static HttpClient Http(HttpMessageHandler handler, string baseAddress) => new(handler) { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(5) };

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] headers)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            map[name] = value;
        }

        return map;
    }

    private static string StripeSign(string body, long timestamp, string secret = StripeSecret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));

    private static string RazorpaySign(string body, string secret = RazorpaySecret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    private static WebhookParseResult StripeVerify(StripeProvider provider, string body, string header) =>
        provider.VerifyAndParseWebhook(Headers(("Stripe-Signature", header)), Encoding.UTF8.GetBytes(body));

    private static WebhookParseResult RazorpayVerify(RazorpayProvider provider, string body, string? signature, string? eventId = "evt-1") =>
        provider.VerifyAndParseWebhook(WithSignature(signature).WithEvent(eventId), Encoding.UTF8.GetBytes(body));

    private static Dictionary<string, string> WithSignature(string? signature)
    {
        var headers = Headers();
        if (signature is not null)
        {
            headers["X-Razorpay-Signature"] = signature;
        }

        return headers;
    }

    // ---- Stripe signatures ----

    [Fact]
    public void The_stripe_signature_matches_the_independently_computed_vector()
    {
        StripeSign(StripeBody, 1_700_000_000).ShouldBe(StripeVectorSignature);

        var result = StripeVerify(Stripe(), StripeBody, $"t=1700000000,v1={StripeVectorSignature}");

        result.Status.ShouldBe(WebhookParseStatus.Parsed);
        var evt = result.Event!;
        evt.Provider.ShouldBe("stripe");
        evt.EventId.ShouldBe("evt_1PabcDEFghiJKLmn");
        evt.Type.ShouldBe(PaymentEventType.PaymentSucceeded);
        evt.OrderId.ShouldBe(Guid.Parse(OrderId));
        evt.ProviderPaymentId.ShouldBe("pi_3Pabc");
        evt.AmountMinor.ShouldBe(118_000);
        evt.Currency.ShouldBe("INR");
    }

    [Fact]
    public void A_tampered_stripe_body_is_rejected()
    {
        var tampered = StripeBody.Replace("118000", "100", StringComparison.Ordinal);

        StripeVerify(Stripe(), tampered, $"t=1700000000,v1={StripeVectorSignature}").Status.ShouldBe(WebhookParseStatus.InvalidSignature);
    }

    [Fact]
    public void A_stripe_signature_made_with_another_secret_is_rejected()
    {
        var forged = StripeSign(StripeBody, 1_700_000_000, "whsec_somebody_else");

        StripeVerify(Stripe(), StripeBody, $"t=1700000000,v1={forged}").Status.ShouldBe(WebhookParseStatus.InvalidSignature);
    }

    [Theory]
    [InlineData(299, true)]
    [InlineData(-299, true)]
    [InlineData(301, false)]
    [InlineData(-301, false)]
    [InlineData(86_400, false)]
    public void A_stripe_timestamp_outside_the_tolerance_is_a_replay(int secondsFromNow, bool accepted)
    {
        var time = new FakeTimeProvider(VectorTime.AddSeconds(secondsFromNow));

        var result = StripeVerify(Stripe(time: time), StripeBody, $"t=1700000000,v1={StripeVectorSignature}");

        result.Status.ShouldBe(accepted ? WebhookParseStatus.Parsed : WebhookParseStatus.InvalidSignature);
    }

    [Fact]
    public void A_stripe_signature_cannot_be_moved_to_a_newer_timestamp()
    {
        // A captured, old (valid) signature re-labelled with a fresh timestamp: the timestamp is part of the signed text.
        var now = VectorTime.AddDays(30);
        var result = StripeVerify(Stripe(time: new FakeTimeProvider(now)), StripeBody, $"t={now.ToUnixTimeSeconds()},v1={StripeVectorSignature}");

        result.Status.ShouldBe(WebhookParseStatus.InvalidSignature);
    }

    [Theory]
    [InlineData("")]
    [InlineData("t=1700000000")]
    [InlineData("v1=abc")]
    [InlineData("t=abc,v1=00")]
    [InlineData("t=1700000000,v1=zz")]
    [InlineData("t=1700000000,v1=")]
    [InlineData("t=-5,v1=00")]
    [InlineData("garbage")]
    public void A_malformed_stripe_header_is_rejected_without_throwing(string header)
    {
        StripeVerify(Stripe(), StripeBody, header).Status.ShouldBe(WebhookParseStatus.InvalidSignature);
    }

    [Fact]
    public void A_missing_stripe_header_is_rejected()
    {
        Stripe().VerifyAndParseWebhook(Headers(), Encoding.UTF8.GetBytes(StripeBody)).Status.ShouldBe(WebhookParseStatus.InvalidSignature);
    }

    [Fact]
    public void Any_matching_v1_signature_is_enough_and_unknown_schemes_are_ignored()
    {
        var header = $"t=1700000000,v0=deadbeef,v1={new string('0', 64)},v1={StripeVectorSignature}";

        StripeVerify(Stripe(), StripeBody, header).Status.ShouldBe(WebhookParseStatus.Parsed);
    }

    [Fact]
    public void The_stripe_signature_is_checked_before_the_body_is_read()
    {
        // Not even valid JSON: with a bad signature the answer is "invalid signature", never "malformed" (nothing is parsed unauthenticated).
        StripeVerify(Stripe(), "{ this is not json", $"t=1700000000,v1={new string('a', 64)}").Status.ShouldBe(WebhookParseStatus.InvalidSignature);

        // With a good signature over unreadable content it is malformed.
        var garbage = "{ this is not json";
        StripeVerify(Stripe(), garbage, $"t=1700000000,v1={StripeSign(garbage, 1_700_000_000)}").Status.ShouldBe(WebhookParseStatus.Malformed);
    }

    [Fact]
    public void A_replayed_stripe_event_carries_the_same_event_id_so_the_ledger_can_recognise_it()
    {
        var first = StripeVerify(Stripe(), StripeBody, $"t=1700000000,v1={StripeVectorSignature}").Event!;
        var second = StripeVerify(Stripe(), StripeBody, $"t=1700000000,v1={StripeVectorSignature}").Event!;

        second.EventId.ShouldBe(first.EventId);
        second.Provider.ShouldBe(first.Provider);
    }

    [Theory]
    [InlineData("checkout.session.completed", "unpaid", WebhookParseStatus.Ignored)]
    [InlineData("checkout.session.async_payment_succeeded", "paid", WebhookParseStatus.Parsed)]
    [InlineData("checkout.session.async_payment_failed", "unpaid", WebhookParseStatus.Parsed)]
    [InlineData("checkout.session.expired", "unpaid", WebhookParseStatus.Parsed)]
    [InlineData("customer.created", "unpaid", WebhookParseStatus.Ignored)]
    public void Stripe_event_types_map_to_the_events_we_act_on(string type, string paymentStatus, WebhookParseStatus expected)
    {
        var body = $"{{\"id\":\"evt_x\",\"type\":\"{type}\",\"data\":{{\"object\":{{\"id\":\"cs_1\",\"client_reference_id\":\"{OrderId}\",\"payment_status\":\"{paymentStatus}\",\"amount_total\":500,\"currency\":\"usd\",\"payment_intent\":\"pi_1\"}}}}}}";

        var result = StripeVerify(Stripe(), body, $"t=1700000000,v1={StripeSign(body, 1_700_000_000)}");

        result.Status.ShouldBe(expected);
    }

    [Fact]
    public void A_stripe_refund_event_carries_the_refund_id()
    {
        var body = $"{{\"id\":\"evt_r\",\"type\":\"refund.created\",\"data\":{{\"object\":{{\"id\":\"re_1\",\"payment_intent\":\"pi_1\",\"amount\":500,\"currency\":\"usd\",\"metadata\":{{\"orderId\":\"{OrderId}\"}}}}}}}}";

        var evt = StripeVerify(Stripe(), body, $"t=1700000000,v1={StripeSign(body, 1_700_000_000)}").Event!;

        evt.Type.ShouldBe(PaymentEventType.RefundCreated);
        evt.ProviderRefundId.ShouldBe("re_1");
        evt.OrderId.ShouldBe(Guid.Parse(OrderId));
    }

    // ---- Razorpay signatures ----

    [Fact]
    public void The_razorpay_signature_matches_the_independently_computed_vector()
    {
        RazorpaySign(RazorpayBody).ShouldBe(RazorpayVectorSignature);

        var result = RazorpayVerify(Razorpay(), RazorpayBody, RazorpayVectorSignature, "evt_Rzp1");

        result.Status.ShouldBe(WebhookParseStatus.Parsed);
        var evt = result.Event!;
        evt.Provider.ShouldBe("razorpay");
        evt.EventId.ShouldBe("evt_Rzp1");
        evt.Type.ShouldBe(PaymentEventType.PaymentSucceeded);
        evt.OrderId.ShouldBe(Guid.Parse(OrderId));
        evt.ProviderPaymentId.ShouldBe("pay_Pdef456");
        evt.AmountMinor.ShouldBe(118_000);
        evt.Currency.ShouldBe("INR");
    }

    [Fact]
    public void A_tampered_razorpay_body_is_rejected()
    {
        RazorpayVerify(Razorpay(), RazorpayBody.Replace("118000", "100", StringComparison.Ordinal), RazorpayVectorSignature).Status
            .ShouldBe(WebhookParseStatus.InvalidSignature);
    }

    [Fact]
    public void A_razorpay_signature_made_with_another_secret_is_rejected()
    {
        RazorpayVerify(Razorpay(), RazorpayBody, RazorpaySign(RazorpayBody, "someone-elses-secret")).Status.ShouldBe(WebhookParseStatus.InvalidSignature);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("99f7")]
    public void A_missing_or_malformed_razorpay_signature_is_rejected(string? signature)
    {
        RazorpayVerify(Razorpay(), RazorpayBody, signature).Status.ShouldBe(WebhookParseStatus.InvalidSignature);
    }

    [Fact]
    public void Razorpay_signatures_are_accepted_in_upper_case_hex_too()
    {
        RazorpayVerify(Razorpay(), RazorpayBody, RazorpayVectorSignature.ToUpperInvariant()).Status.ShouldBe(WebhookParseStatus.Parsed);
    }

    [Fact]
    public void Razorpay_has_no_timestamp_so_replays_are_recognised_by_the_event_id()
    {
        var later = new FakeTimeProvider(VectorTime.AddDays(90));

        var first = RazorpayVerify(Razorpay(time: later), RazorpayBody, RazorpayVectorSignature, "evt_same").Event!;
        var second = RazorpayVerify(Razorpay(time: later), RazorpayBody, RazorpayVectorSignature, "evt_same").Event!;

        second.EventId.ShouldBe(first.EventId); // the unique (provider, event id) ledger turns the second delivery into a no-op
    }

    [Fact]
    public void Without_an_event_id_header_a_stable_digest_of_the_authenticated_body_stands_in()
    {
        var a = RazorpayVerify(Razorpay(), RazorpayBody, RazorpayVectorSignature, eventId: null).Event!;
        var b = RazorpayVerify(Razorpay(), RazorpayBody, RazorpayVectorSignature, eventId: null).Event!;

        a.EventId.ShouldStartWith("body-");
        a.EventId.ShouldBe(b.EventId);
    }

    [Theory]
    [InlineData("payment_link.expired", PaymentEventType.CheckoutExpired)]
    [InlineData("payment_link.cancelled", PaymentEventType.CheckoutExpired)]
    public void Razorpay_expiry_events_map_to_checkout_expired(string type, PaymentEventType expected)
    {
        var body = $"{{\"event\":\"{type}\",\"payload\":{{\"payment_link\":{{\"entity\":{{\"id\":\"plink_1\",\"reference_id\":\"{OrderId}\"}}}}}}}}";

        var evt = RazorpayVerify(Razorpay(), body, RazorpaySign(body)).Event!;

        evt.Type.ShouldBe(expected);
        evt.OrderId.ShouldBe(Guid.Parse(OrderId));
    }

    [Fact]
    public void Razorpay_refund_and_unrelated_events_are_mapped_or_ignored()
    {
        var refund = $"{{\"event\":\"refund.created\",\"payload\":{{\"refund\":{{\"entity\":{{\"id\":\"rfnd_1\",\"payment_id\":\"pay_1\",\"amount\":500,\"currency\":\"INR\",\"notes\":{{\"orderId\":\"{OrderId}\"}}}}}}}}}}";
        var other = "{\"event\":\"settlement.processed\",\"payload\":{}}";

        var evt = RazorpayVerify(Razorpay(), refund, RazorpaySign(refund)).Event!;
        evt.Type.ShouldBe(PaymentEventType.RefundCreated);
        evt.ProviderRefundId.ShouldBe("rfnd_1");
        RazorpayVerify(Razorpay(), other, RazorpaySign(other)).Status.ShouldBe(WebhookParseStatus.Ignored);
    }

    [Fact]
    public void A_validly_signed_razorpay_event_without_a_type_is_malformed()
    {
        var body = "{\"payload\":{}}";

        RazorpayVerify(Razorpay(), body, RazorpaySign(body)).Status.ShouldBe(WebhookParseStatus.Malformed);
    }

    // ---- Stripe API calls ----

    private static CheckoutOrder Order(long subtotal = 100_000, long tax = 18_000) =>
        new(Guid.Parse(OrderId), "Growth", 5_000, subtotal, tax, subtotal + tax, "INR", 18m, "GST", "Acme Pvt Ltd", "billing@acme.test", VectorTime.UtcDateTime.AddHours(2));

    [Fact]
    public async Task Stripe_checkout_sends_the_order_amounts_and_never_card_data()
    {
        var handler = new StubHandler { Response = _ => Json("{\"id\":\"cs_test_1\",\"url\":\"https://checkout.stripe.test/pay/cs_test_1\",\"expires_at\":1700007200}") };

        var session = await Stripe(handler).CreateCheckoutAsync(Order(), new Uri("https://portal.test/billing/success?order=1"), new Uri("https://portal.test/billing/cancelled?order=1"), default);

        session.SessionId.ShouldBe("cs_test_1");
        session.CheckoutUrl.ShouldBe("https://checkout.stripe.test/pay/cs_test_1");
        session.ExpiresAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1_700_007_200).UtcDateTime);

        var call = handler.Calls.Single();
        call.Request.Method.ShouldBe(HttpMethod.Post);
        call.Request.RequestUri!.AbsoluteUri.ShouldBe("https://api.stripe.test/v1/checkout/sessions");
        call.Request.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        call.Request.Headers.GetValues("Idempotency-Key").Single().ShouldBe("nexa-checkout-" + OrderId.Replace("-", string.Empty, StringComparison.Ordinal));
        var form = Form(call.Body);
        form["mode"].ShouldBe("payment");
        form["client_reference_id"].ShouldBe(OrderId);
        form["metadata[orderId]"].ShouldBe(OrderId);
        form["line_items[0][price_data][currency]"].ShouldBe("inr");
        form["line_items[0][price_data][unit_amount]"].ShouldBe("100000");
        form["line_items[1][price_data][unit_amount]"].ShouldBe("18000");
        form["line_items[1][price_data][product_data][name]"].ShouldBe("GST 18%");
        form["customer_email"].ShouldBe("billing@acme.test");
        form["success_url"].ShouldBe("https://portal.test/billing/success?order=1");
        form.Keys.ShouldNotContain(k => k.Contains("card", StringComparison.OrdinalIgnoreCase));
        call.Body.ShouldNotContain("sk_test_key"); // the key travels in the Authorization header only
    }

    [Fact]
    public async Task Stripe_checkout_without_tax_has_one_line_item()
    {
        var handler = new StubHandler { Response = _ => Json("{\"id\":\"cs_1\",\"url\":\"https://checkout.stripe.test/p\"}") };

        await Stripe(handler).CreateCheckoutAsync(Order(tax: 0), new Uri("https://p.test/s"), new Uri("https://p.test/c"), default);

        Form(handler.Calls.Single().Body).Keys.ShouldNotContain("line_items[1][quantity]");
    }

    [Fact]
    public async Task Stripe_session_lifetime_is_kept_within_what_stripe_allows()
    {
        var handler = new StubHandler { Response = _ => Json("{\"id\":\"cs_1\",\"url\":\"https://checkout.stripe.test/p\"}") };
        var soon = Order() with { ExpiresAt = VectorTime.UtcDateTime.AddMinutes(1) };
        var far = Order() with { ExpiresAt = VectorTime.UtcDateTime.AddDays(10) };

        await Stripe(handler).CreateCheckoutAsync(soon, new Uri("https://p.test/s"), new Uri("https://p.test/c"), default);
        await Stripe(handler).CreateCheckoutAsync(far, new Uri("https://p.test/s"), new Uri("https://p.test/c"), default);

        long.Parse(Form(handler.Calls[0].Body)["expires_at"], CultureInfo.InvariantCulture).ShouldBe(VectorTime.AddMinutes(30).ToUnixTimeSeconds());
        long.Parse(Form(handler.Calls[1].Body)["expires_at"], CultureInfo.InvariantCulture).ShouldBe(VectorTime.AddHours(24).ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData("{\"id\":\"cs_1\"}")]
    [InlineData("{\"url\":\"https://x.test\"}")]
    [InlineData("{\"id\":\"cs_1\",\"url\":\"http://insecure.test/p\"}")]
    [InlineData("{}")]
    public async Task A_stripe_answer_without_a_secure_checkout_page_is_a_provider_failure(string answer)
    {
        var handler = new StubHandler { Response = _ => Json(answer) };

        await Should.ThrowAsync<PaymentProviderException>(() =>
            Stripe(handler).CreateCheckoutAsync(Order(), new Uri("https://p.test/s"), new Uri("https://p.test/c"), default));
    }

    [Fact]
    public async Task A_stripe_error_becomes_a_provider_exception_that_carries_neither_keys_nor_the_response_body()
    {
        var handler = new StubHandler
        {
            Response = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":{\"code\":\"parameter_invalid_integer\",\"message\":\"secret detail sk_test_key 4242 4242\"}}"),
            },
        };

        var ex = await Should.ThrowAsync<PaymentProviderException>(() =>
            Stripe(handler).CreateCheckoutAsync(Order(), new Uri("https://p.test/s"), new Uri("https://p.test/c"), default));

        ex.Message.ShouldContain("400");
        ex.Message.ShouldContain("parameter_invalid_integer");
        ex.Message.ShouldNotContain("sk_test_key");
        ex.Message.ShouldNotContain("4242");
    }

    [Fact]
    public async Task A_stripe_network_failure_and_a_timeout_are_provider_exceptions()
    {
        await Should.ThrowAsync<PaymentProviderException>(() =>
            Stripe(new StubHandler { Response = _ => throw new HttpRequestException("connection refused") })
                .CreateCheckoutAsync(Order(), new Uri("https://p.test/s"), new Uri("https://p.test/c"), default));
        await Should.ThrowAsync<PaymentProviderException>(() =>
            Stripe(new StubHandler { Response = _ => throw new TaskCanceledException("timeout") })
                .CreateCheckoutAsync(Order(), new Uri("https://p.test/s"), new Uri("https://p.test/c"), default));
    }

    [Fact]
    public async Task A_cancelled_request_is_not_disguised_as_a_provider_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var thrown = await Record.ExceptionAsync(() =>
            Stripe(new StubHandler { Response = _ => Json("{}") }).CreateCheckoutAsync(Order(), new Uri("https://p.test/s"), new Uri("https://p.test/c"), cts.Token));

        thrown.ShouldBeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task Stripe_refunds_use_the_refund_id_as_idempotency_key()
    {
        var handler = new StubHandler { Response = _ => Json("{\"id\":\"re_1\",\"status\":\"succeeded\"}") };

        var refund = await Stripe(handler).RefundAsync("pi_1", 5_000, "INR", "duplicate", "abc123", default);

        refund.RefundId.ShouldBe("re_1");
        var call = handler.Calls.Single();
        call.Request.RequestUri!.AbsoluteUri.ShouldBe("https://api.stripe.test/v1/refunds");
        call.Request.Headers.GetValues("Idempotency-Key").Single().ShouldBe("nexa-refund-abc123");
        var form = Form(call.Body);
        form["payment_intent"].ShouldBe("pi_1");
        form["amount"].ShouldBe("5000");
    }

    [Theory]
    [InlineData("{\"id\":\"re_1\",\"status\":\"failed\"}")]
    [InlineData("{\"status\":\"succeeded\"}")]
    public async Task A_refund_the_provider_did_not_accept_is_a_failure(string answer)
    {
        await Should.ThrowAsync<PaymentProviderException>(() =>
            Stripe(new StubHandler { Response = _ => Json(answer) }).RefundAsync("pi_1", 5_000, "INR", "x", "id", default));
    }

    [Theory]
    [InlineData("{\"payment_status\":\"paid\",\"status\":\"complete\",\"payment_intent\":\"pi_9\",\"amount_total\":118000,\"currency\":\"inr\"}", ProviderPaymentStatus.Paid, "pi_9", 118_000L, "INR")]
    [InlineData("{\"payment_status\":\"unpaid\",\"status\":\"open\"}", ProviderPaymentStatus.Pending, null, null, null)]
    [InlineData("{\"payment_status\":\"unpaid\",\"status\":\"expired\"}", ProviderPaymentStatus.Expired, null, null, null)]
    public async Task Stripe_fetch_maps_the_session_state(string answer, ProviderPaymentStatus status, string? paymentId, long? amount, string? currency)
    {
        var handler = new StubHandler { Response = _ => Json(answer) };

        var state = await Stripe(handler).FetchPaymentAsync("cs_test_1", default);

        state.Status.ShouldBe(status);
        state.PaymentId.ShouldBe(paymentId);
        state.AmountMinor.ShouldBe(amount);
        state.Currency.ShouldBe(currency);
        handler.Calls.Single().Request.Method.ShouldBe(HttpMethod.Get);
        handler.Calls.Single().Request.RequestUri!.AbsoluteUri.ShouldBe("https://api.stripe.test/v1/checkout/sessions/cs_test_1");
    }

    [Fact]
    public async Task Stripe_checkout_can_be_closed()
    {
        var handler = new StubHandler { Response = _ => Json("{\"id\":\"cs_1\",\"status\":\"expired\"}") };

        await Stripe(handler).CancelCheckoutAsync("cs_1", default);

        handler.Calls.Single().Request.RequestUri!.AbsoluteUri.ShouldBe("https://api.stripe.test/v1/checkout/sessions/cs_1/expire");
    }

    // ---- Razorpay API calls ----

    [Fact]
    public async Task Razorpay_checkout_creates_a_payment_link_with_basic_auth_and_the_order_amount()
    {
        var handler = new StubHandler { Response = _ => Json("{\"id\":\"plink_1\",\"short_url\":\"https://rzp.test/i/abc\",\"expire_by\":1700007200}") };

        var session = await Razorpay(handler).CreateCheckoutAsync(Order(), new Uri("https://portal.test/billing/success?order=1"), new Uri("https://portal.test/c"), default);

        session.SessionId.ShouldBe("plink_1");
        session.CheckoutUrl.ShouldBe("https://rzp.test/i/abc");
        var call = handler.Calls.Single();
        call.Request.RequestUri!.AbsoluteUri.ShouldBe("https://api.razorpay.test/v1/payment_links");
        call.Request.Headers.Authorization!.Scheme.ShouldBe("Basic");
        Encoding.UTF8.GetString(Convert.FromBase64String(call.Request.Headers.Authorization.Parameter!)).ShouldBe("rzp_test_id:rzp_test_secret");
        using var json = JsonDocument.Parse(call.Body);
        json.RootElement.GetProperty("amount").GetInt64().ShouldBe(118_000);
        json.RootElement.GetProperty("currency").GetString().ShouldBe("INR");
        json.RootElement.GetProperty("reference_id").GetString().ShouldBe(OrderId);
        json.RootElement.GetProperty("notes").GetProperty("orderId").GetString().ShouldBe(OrderId);
        json.RootElement.GetProperty("callback_url").GetString().ShouldBe("https://portal.test/billing/success?order=1");
        json.RootElement.GetProperty("accept_partial").GetBoolean().ShouldBeFalse();
        call.Body.ShouldNotContain("rzp_test_secret");
    }

    [Theory]
    [InlineData("{\"status\":\"paid\",\"amount_paid\":118000,\"currency\":\"INR\",\"payments\":[{\"payment_id\":\"pay_7\",\"amount\":118000,\"status\":\"captured\"}]}", ProviderPaymentStatus.Paid, "pay_7", 118_000L)]
    [InlineData("{\"status\":\"created\"}", ProviderPaymentStatus.Pending, null, null)]
    [InlineData("{\"status\":\"partially_paid\"}", ProviderPaymentStatus.Pending, null, null)]
    [InlineData("{\"status\":\"expired\"}", ProviderPaymentStatus.Expired, null, null)]
    [InlineData("{\"status\":\"cancelled\"}", ProviderPaymentStatus.Expired, null, null)]
    public async Task Razorpay_fetch_maps_the_payment_link_state(string answer, ProviderPaymentStatus status, string? paymentId, long? amount)
    {
        var handler = new StubHandler { Response = _ => Json(answer) };

        var state = await Razorpay(handler).FetchPaymentAsync("plink_1", default);

        state.Status.ShouldBe(status);
        state.PaymentId.ShouldBe(paymentId);
        state.AmountMinor.ShouldBe(amount);
    }

    [Fact]
    public async Task Razorpay_refunds_carry_the_idempotency_header()
    {
        var handler = new StubHandler { Response = _ => Json("{\"id\":\"rfnd_1\",\"status\":\"processed\"}") };

        var refund = await Razorpay(handler).RefundAsync("pay_1", 5_000, "INR", "duplicate", "abc123", default);

        refund.RefundId.ShouldBe("rfnd_1");
        var call = handler.Calls.Single();
        call.Request.RequestUri!.AbsoluteUri.ShouldBe("https://api.razorpay.test/v1/payments/pay_1/refund");
        call.Request.Headers.GetValues("X-Refund-Idempotency").Single().ShouldBe("abc123");
        JsonDocument.Parse(call.Body).RootElement.GetProperty("amount").GetInt64().ShouldBe(5_000);
    }

    // ---- simulated provider ----

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public FakeEnvironment(string name)
        {
            EnvironmentName = name;
        }

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = "/";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Testing", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData("UiDemo", false)]
    public async Task The_simulated_provider_exists_only_in_development_and_testing(string environment, bool allowed)
    {
        var portal = Microsoft.Extensions.Options.Options.Create(new PortalLinksOptions { PublicBaseUrl = "https://localhost:7200" });
        Func<SimulatedProvider> create = () => new SimulatedProvider(new FakeEnvironment(environment), portal);

        if (allowed)
        {
            var provider = create();
            (await provider.CreateCheckoutAsync(Order(), new Uri("https://p.test/s"), new Uri("https://p.test/c"), default)).CheckoutUrl
                .ShouldBe("https://localhost:7200/dev/pay/" + OrderId);
            provider.VerifyAndParseWebhook(Headers(), [1]).Status.ShouldBe(WebhookParseStatus.InvalidSignature);
        }
        else
        {
            Should.Throw<InvalidOperationException>(create);
        }
    }

    // ---- helpers ----

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Dictionary<string, string> Form(string body) =>
        body.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Response { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));
            return Response(request);
        }
    }
}

internal static class HeaderExtensions
{
    public static Dictionary<string, string> WithEvent(this Dictionary<string, string> headers, string? eventId)
    {
        if (eventId is not null)
        {
            headers["X-Razorpay-Event-Id"] = eventId;
        }

        return headers;
    }
}
