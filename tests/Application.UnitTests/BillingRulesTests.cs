using System.Text.Json;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Billing;
using NexaVerify.Application.Licensing;
using NexaVerify.Contracts.Billing;
using NexaVerify.Domain.Billing;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.UnitTests;

public class BillingRulesTests
{
    private static readonly BillingOptionsValidator Validator = new();

    private static BillingOptions Stripe() => new()
    {
        Enabled = true,
        Provider = "Stripe",
        Stripe = { SecretKey = "sk_test_abc", WebhookSecret = "whsec_abc" },
    };

    // ---- configuration ----

    [Fact]
    public void Billing_that_is_off_needs_no_provider_settings()
    {
        Validator.Validate(null, new BillingOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Enabled_billing_needs_a_known_provider_and_its_secrets()
    {
        Validator.Validate(null, new BillingOptions { Enabled = true }).Failed.ShouldBeTrue();
        Validator.Validate(null, new BillingOptions { Enabled = true, Provider = "Paypal" }).Failed.ShouldBeTrue();
        Validator.Validate(null, Stripe()).Succeeded.ShouldBeTrue();

        var noSecret = Stripe();
        noSecret.Stripe.WebhookSecret = " ";
        Validator.Validate(null, noSecret).Failed.ShouldBeTrue();

        var razorpay = new BillingOptions { Enabled = true, Provider = "razorpay", Razorpay = { KeyId = "rzp_test_1", KeySecret = "s" } };
        var result = Validator.Validate(null, razorpay);
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("WebhookSecret");
        razorpay.Razorpay.WebhookSecret = "w";
        Validator.Validate(null, razorpay).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validation_messages_name_the_missing_setting_but_never_print_a_secret()
    {
        var options = Stripe();
        options.Stripe.ApiBaseUrl = "http://insecure.example";

        var result = Validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("ApiBaseUrl");
        result.FailureMessage.ShouldNotContain("sk_test_abc");
        result.FailureMessage.ShouldNotContain("whsec_abc");
    }

    [Theory]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("us1")]
    public void Currencies_must_be_iso_codes(string code)
    {
        var options = Stripe();
        options.AllowedCurrencies = ["INR", code];

        Validator.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void The_currency_allow_list_ignores_case()
    {
        var options = new BillingOptions { AllowedCurrencies = ["INR", "usd"] };

        options.AllowsCurrency("inr").ShouldBeTrue();
        options.AllowsCurrency("USD").ShouldBeTrue();
        options.AllowsCurrency("EUR").ShouldBeFalse();
    }

    [Theory]
    [InlineData("Stripe", "sk_test_123", null, "sandbox")]
    [InlineData("Stripe", "rk_live_123", null, "live")]
    [InlineData("Stripe", null, null, "none")]
    [InlineData("Razorpay", null, "rzp_test_abc", "sandbox")]
    [InlineData("Razorpay", null, "rzp_live_abc", "live")]
    [InlineData("Simulated", null, null, "simulated")]
    [InlineData("None", null, null, "none")]
    public void The_mode_is_read_from_the_shape_of_the_key(string provider, string? stripeKey, string? razorpayKey, string expected)
    {
        var options = new BillingOptions { Provider = provider, Stripe = { SecretKey = stripeKey }, Razorpay = { KeyId = razorpayKey } };

        options.Mode().ShouldBe(expected);
    }

    // ---- money ----

    [Theory]
    [InlineData(118_000L, "INR", "INR 1,180.00")]
    [InlineData(5L, "USD", "USD 0.05")]
    [InlineData(1_234_567L, "usd", "USD 12,345.67")]
    [InlineData(5_000L, "JPY", "JPY 5,000")]
    [InlineData(1_500L, "KWD", "KWD 1.500")]
    public void Amounts_are_formatted_by_the_currency_exponent(long minor, string currency, string expected)
    {
        Money.Format(minor, currency).ShouldBe(expected);
    }

    // ---- invoice ----

    private static PaymentOrder PaidOrder(string packName = "Growth", string buyerJson = "{}")
    {
        var pack = CreditPack.Create(packName, null, 5_000, 90, 100_000, "INR", null, 0, true, true);
        return PaymentOrder.Create(Guid.NewGuid(), pack, 18m, "GST", "stripe", null, buyerJson, DateTime.UtcNow);
    }

    [Fact]
    public void The_invoice_shows_the_tax_breakdown_seller_and_buyer()
    {
        var buyer = new BuyerSnapshot("Acme Pvt Ltd", "1 MG Road", null, "Bengaluru", "Karnataka", "560001", "IN", "29ABCDE1234F1Z5", "billing@acme.test");
        var seller = new SellerOptions { LegalName = "NexaVerify Pvt Ltd", AddressLines = ["Tower 1", "Mumbai"], TaxId = "27AAAAA0000A1Z5", TaxIdLabel = "GSTIN", InvoiceFooter = "Thank you" };

        var invoice = InvoiceRenderer.Render(PaidOrder(buyerJson: buyer.ToJson()), "Acme", [], seller);

        invoice.Html.ShouldContain("Tax invoice");
        invoice.Html.ShouldContain("NexaVerify Pvt Ltd");
        invoice.Html.ShouldContain("GSTIN: 27AAAAA0000A1Z5");
        invoice.Html.ShouldContain("Acme Pvt Ltd");
        invoice.Html.ShouldContain("Bengaluru, Karnataka, 560001");
        invoice.Html.ShouldContain("Tax ID: 29ABCDE1234F1Z5");
        invoice.Html.ShouldContain("INR 1,000.00");
        invoice.Html.ShouldContain("GST 18%");
        invoice.Html.ShouldContain("INR 180.00");
        invoice.Html.ShouldContain("INR 1,180.00");
        invoice.Html.ShouldContain("Thank you");
        invoice.FileName.ShouldEndWith(".html");
    }

    [Fact]
    public void Every_dynamic_value_on_the_invoice_is_html_encoded()
    {
        const string attack = "<img src=x onerror=alert(1)>";
        var buyer = new BuyerSnapshot(attack, attack, null, attack, null, "1", "IN", attack, attack);
        var seller = new SellerOptions { LegalName = attack, AddressLines = [attack], TaxId = attack, Email = attack, InvoiceFooter = attack };

        var invoice = InvoiceRenderer.Render(PaidOrder(packName: attack, buyerJson: buyer.ToJson()), attack, [], seller);

        invoice.Html.ShouldNotContain("<img");
        invoice.Html.ShouldNotContain("<script");
        invoice.Html.ShouldContain("&lt;img src=x onerror=alert(1)&gt;");
    }

    [Fact]
    public void A_broken_buyer_snapshot_falls_back_to_the_client_name()
    {
        var invoice = InvoiceRenderer.Render(PaidOrder(buyerJson: "{not json"), "Fallback Client", [], new SellerOptions());

        invoice.Html.ShouldContain("Fallback Client");
    }

    [Fact]
    public void The_buyer_snapshot_round_trips()
    {
        var snapshot = new BuyerSnapshot("Acme", "Street", "Floor 2", "City", null, "1", "IN", null, "a@b.test");

        BuyerSnapshot.Parse(snapshot.ToJson()).ShouldBe(snapshot);
        BuyerSnapshot.Parse("").ShouldBe(BuyerSnapshot.Empty);
    }

    // ---- request validators ----

    [Fact]
    public void A_checkout_request_validates_the_pack_id_and_the_idempotency_key()
    {
        var validator = new CheckoutRequestValidator();

        validator.Validate(new CheckoutRequest(Guid.NewGuid(), null)).IsValid.ShouldBeTrue();
        validator.Validate(new CheckoutRequest(Guid.NewGuid(), "order-2026.01:a_b")).IsValid.ShouldBeTrue();
        validator.Validate(new CheckoutRequest(Guid.Empty, null)).IsValid.ShouldBeFalse();
        validator.Validate(new CheckoutRequest(Guid.NewGuid(), new string('k', 101))).IsValid.ShouldBeFalse();
        validator.Validate(new CheckoutRequest(Guid.NewGuid(), "has space")).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_pack_request_is_checked_field_by_field()
    {
        var validator = new CreateCreditPackRequestValidator();
        var valid = new CreateCreditPackRequest("Starter", null, 100, 30, 49_900, "INR", ["a"], 0, true, true);

        validator.Validate(valid).IsValid.ShouldBeTrue();
        validator.Validate(valid with { Name = "" }).IsValid.ShouldBeFalse();
        validator.Validate(valid with { Credits = 0 }).IsValid.ShouldBeFalse();
        validator.Validate(valid with { ValidityDays = 4000 }).IsValid.ShouldBeFalse();
        validator.Validate(valid with { PriceMinor = 0 }).IsValid.ShouldBeFalse();
        validator.Validate(valid with { Currency = "rupee" }).IsValid.ShouldBeFalse();
        validator.Validate(valid with { Highlights = Enumerable.Repeat("x", 9).ToList() }).IsValid.ShouldBeFalse();
        new UpdateCreditPackRequestValidator().Validate(new UpdateCreditPackRequest("n", null, 1, 1, 1, "USD", null, 0, true, true)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_refund_request_needs_a_reason_and_a_positive_amount()
    {
        var validator = new RefundOrderRequestValidator();

        validator.Validate(new RefundOrderRequest(null, "duplicate")).IsValid.ShouldBeTrue();
        validator.Validate(new RefundOrderRequest(500, "duplicate")).IsValid.ShouldBeTrue();
        validator.Validate(new RefundOrderRequest(0, "duplicate")).IsValid.ShouldBeFalse();
        validator.Validate(new RefundOrderRequest(-5, "duplicate")).IsValid.ShouldBeFalse();
        validator.Validate(new RefundOrderRequest(null, "")).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_billing_profile_request_needs_the_invoice_essentials()
    {
        var validator = new UpdateBillingProfileRequestValidator();
        var valid = new UpdateBillingProfileRequest("Acme", "1 Road", null, "City", null, "560001", "IN", "29ABCDE1234F1Z5", "billing@acme.test");

        validator.Validate(valid).IsValid.ShouldBeTrue();
        validator.Validate(valid with { LegalName = "" }).IsValid.ShouldBeFalse();
        validator.Validate(valid with { Country = "India" }).IsValid.ShouldBeFalse();
        validator.Validate(valid with { BillingEmail = "nope" }).IsValid.ShouldBeFalse();
        validator.Validate(valid with { TaxId = "<script>" }).IsValid.ShouldBeFalse();
        validator.Validate(valid with { TaxId = null }).IsValid.ShouldBeTrue();
    }

    // ---- alerts carry a billing link while billing is on ----

    private static readonly DateTime Now = new(2030, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static LicenseAlertCandidate Candidate(int total, int consumed, int daysLeft) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Trial", LicenseStatus.Active, total, consumed, Now.AddDays(-10), Now.AddDays(daysLeft));

    [Fact]
    public void Low_balance_and_trial_expiry_alerts_point_to_the_billing_page_when_billing_is_on()
    {
        const string link = "https://portal.example.com/billing";
        var options = new LicenseAlertOptions();

        var alerts = LicenseAlertRules.Evaluate(Candidate(100, 95, 3), Now, options, link);

        alerts.Select(a => a.Type).ShouldBe([LicenseAlertType.LowBalance, LicenseAlertType.Expiring], ignoreOrder: true);
        foreach (var alert in alerts)
        {
            alert.Message.ShouldContain(link);
            JsonSerializer.Serialize(alert.Payload).ShouldContain("\"billingUrl\":\"" + link + "\"");
        }
    }

    [Fact]
    public void Alerts_are_unchanged_while_billing_is_off()
    {
        var alerts = LicenseAlertRules.Evaluate(Candidate(100, 95, 3), Now, new LicenseAlertOptions());

        alerts.ShouldNotBeEmpty();
        foreach (var alert in alerts)
        {
            alert.Message.ShouldNotContain("http");
            JsonSerializer.Serialize(alert.Payload).ShouldNotContain("billingUrl");
        }
    }
}
