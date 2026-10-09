using NexaVerify.Domain.Billing;
using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.UnitTests;

public class BillingTests
{
    private static CreditPack Pack(long price = 100_000, string currency = "inr") =>
        CreditPack.Create("Growth", "For teams", 5_000, 90, price, currency, ["5,000 credits", " ", "Email support"], 2, true, true);

    // ---- credit pack ----

    [Fact]
    public void A_pack_normalises_its_currency_and_drops_blank_highlights()
    {
        var pack = Pack();

        pack.Currency.ShouldBe("INR");
        pack.Highlights.ShouldBe(["5,000 credits", "Email support"]);
        pack.IsActive.ShouldBeTrue();
        pack.IsPublic.ShouldBeTrue();
    }

    [Theory]
    [InlineData("", 10, 30, 100L, "USD")]
    [InlineData("Name", 0, 30, 100L, "USD")]
    [InlineData("Name", 10, 0, 100L, "USD")]
    [InlineData("Name", 10, 30, 0L, "USD")]
    [InlineData("Name", 10, 30, -5L, "USD")]
    [InlineData("Name", 10, 30, 100L, "US")]
    [InlineData("Name", 10, 30, 100L, "US1")]
    [InlineData("Name", 10, 3651, 100L, "USD")]
    public void An_invalid_pack_is_refused(string name, int credits, int days, long price, string currency)
    {
        Should.Throw<DomainException>(() => CreditPack.Create(name, null, credits, days, price, currency, null, 0, true, true));
    }

    [Fact]
    public void Too_many_or_too_long_highlights_are_refused()
    {
        Should.Throw<DomainException>(() => CreditPack.Create("n", null, 1, 1, 1, "USD", Enumerable.Repeat("x", 9), 0, true, true));
        Should.Throw<DomainException>(() => CreditPack.Create("n", null, 1, 1, 1, "USD", [new string('x', 121)], 0, true, true));
    }

    // ---- tax and orders ----

    [Theory]
    [InlineData(100_000L, 18, 18_000L)]
    [InlineData(999L, 18, 180L)]       // 179.82 rounds up
    [InlineData(1_005L, 10, 101L)]     // 100.5 rounds half away from zero
    [InlineData(1_004L, 10, 100L)]     // 100.4 rounds down
    [InlineData(5_000L, 0, 0L)]
    public void Tax_is_rounded_to_the_minor_unit(long subtotal, int percent, long expected)
    {
        PaymentOrder.TaxFor(subtotal, percent).ShouldBe(expected);
    }

    [Fact]
    public void An_order_freezes_what_the_customer_was_shown()
    {
        var pack = Pack(100_000);
        var client = Guid.NewGuid();

        var order = PaymentOrder.Create(client, pack, 18m, "GST", "Stripe", "key-1", "{}", new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        pack.Update("Renamed", null, 1, 1, 1, "USD", null, 0, false, false); // a later edit of the pack must not reach the order

        order.ClientId.ShouldBe(client);
        order.PackName.ShouldBe("Growth");
        order.Credits.ShouldBe(5_000);
        order.ValidityDays.ShouldBe(90);
        order.SubtotalMinor.ShouldBe(100_000);
        order.TaxMinor.ShouldBe(18_000);
        order.TotalMinor.ShouldBe(118_000);
        order.Currency.ShouldBe("INR");
        order.TaxLabel.ShouldBe("GST");
        order.Provider.ShouldBe("stripe");
        order.Status.ShouldBe(PaymentOrderStatus.Pending);
        order.IsPayable.ShouldBeTrue();
        order.IsRefundable.ShouldBeFalse();
    }

    [Fact]
    public void An_out_of_range_tax_rate_is_refused()
    {
        Should.Throw<DomainException>(() => PaymentOrder.Create(Guid.NewGuid(), Pack(), 101m, "Tax", "stripe", null, "{}", DateTime.UtcNow));
        Should.Throw<DomainException>(() => PaymentOrder.Create(Guid.NewGuid(), Pack(), -1m, "Tax", "stripe", null, "{}", DateTime.UtcNow));
    }

    [Fact]
    public void Only_unpaid_states_accept_a_payment_and_only_paid_states_a_refund()
    {
        PaymentOrder.PayableStatuses.ShouldBe([PaymentOrderStatus.Pending, PaymentOrderStatus.Expired, PaymentOrderStatus.Failed], ignoreOrder: true);
        PaymentOrder.RefundableStatuses.ShouldBe([PaymentOrderStatus.Paid, PaymentOrderStatus.PartiallyRefunded], ignoreOrder: true);
        PaymentOrder.PayableStatuses.Intersect(PaymentOrder.RefundableStatuses).ShouldBeEmpty();
        PaymentOrder.PayableStatuses.ShouldNotContain(PaymentOrderStatus.Cancelled);
    }

    [Fact]
    public void A_failed_checkout_marks_a_pending_order_failed_once()
    {
        var order = PaymentOrder.Create(Guid.NewGuid(), Pack(), 0m, "Tax", "stripe", null, "{}", DateTime.UtcNow);

        order.FailCheckout(new string('x', 500));

        order.Status.ShouldBe(PaymentOrderStatus.Failed);
        order.FailureReason!.Length.ShouldBe(200);
        Should.Throw<DomainException>(() => order.FailCheckout("again"));
    }

    // ---- refund ----

    [Fact]
    public void A_refund_starts_pending_and_finishes_exactly_once()
    {
        var refund = Refund.Start(Guid.NewGuid(), Guid.NewGuid(), 500, "  duplicate purchase ");

        refund.Status.ShouldBe(RefundStatus.Pending);
        refund.Reason.ShouldBe("duplicate purchase");

        refund.Succeed("re_123", 7, DateTime.UtcNow);
        refund.Status.ShouldBe(RefundStatus.Succeeded);
        refund.CreditsRevoked.ShouldBe(7);
        Should.Throw<DomainException>(() => refund.Succeed("re_999", 1, DateTime.UtcNow));
        Should.Throw<DomainException>(() => refund.Fail(DateTime.UtcNow));
    }

    [Theory]
    [InlineData(0L, "reason")]
    [InlineData(-1L, "reason")]
    [InlineData(10L, "")]
    [InlineData(10L, "   ")]
    public void An_invalid_refund_is_refused(long amount, string reason)
    {
        Should.Throw<DomainException>(() => Refund.Start(Guid.NewGuid(), Guid.NewGuid(), amount, reason));
    }

    // ---- billing profile ----

    [Fact]
    public void A_billing_profile_normalises_country_and_tax_id_and_knows_when_it_is_complete()
    {
        var profile = BillingProfile.Create(Guid.NewGuid(), " Acme Pvt Ltd ", "1 MG Road", null, "Bengaluru", "KA", "560001", "in", "29abcde 1234-f1z5", "billing@acme.test");

        profile.Country.ShouldBe("IN");
        profile.TaxId.ShouldBe("29ABCDE1234F1Z5");
        profile.LegalName.ShouldBe("Acme Pvt Ltd");
        profile.IsComplete(requireTaxId: true).ShouldBeTrue();

        profile.Update("Acme", "1 MG Road", null, "Bengaluru", null, "560001", "IN", null, "billing@acme.test");
        profile.TaxId.ShouldBeNull();
        profile.IsComplete(requireTaxId: false).ShouldBeTrue();
        profile.IsComplete(requireTaxId: true).ShouldBeFalse();
    }

    [Fact]
    public void A_profile_missing_required_parts_is_not_complete()
    {
        BillingProfile.Create(Guid.NewGuid(), "Acme", "", null, "City", null, "1", "IN", null, "a@b.test").IsComplete(false).ShouldBeFalse();
        BillingProfile.Create(Guid.NewGuid(), "Acme", "Street", null, "City", null, "1", "", null, "a@b.test").IsComplete(false).ShouldBeFalse();
        BillingProfile.Create(Guid.NewGuid(), "Acme", "Street", null, "City", null, "1", "IN", null, "").IsComplete(false).ShouldBeFalse();
    }

    [Theory]
    [InlineData("INDIA", "x")]
    [InlineData("I", "x")]
    [InlineData("IN", "<script>")]
    public void A_profile_refuses_a_malformed_country_or_tax_id(string country, string taxId)
    {
        Should.Throw<DomainException>(() => BillingProfile.Create(Guid.NewGuid(), "Acme", "Street", null, "City", null, "1", country, taxId, "a@b.test"));
    }

    // ---- invoice number ----

    [Theory]
    [InlineData(2026, 1, "INV-2026-000001")]
    [InlineData(2026, 123456, "INV-2026-123456")]
    [InlineData(2031, 9, "INV-2031-000009")]
    public void Invoice_numbers_have_a_fixed_shape(int year, int number, string expected)
    {
        InvoiceSequence.Format(year, number).ShouldBe(expected);
    }

    // ---- event ledger ----

    [Fact]
    public void A_payment_event_remembers_its_outcome()
    {
        var evt = PaymentEvent.Receive("Stripe", new string('e', 150), "PaymentSucceeded", Guid.NewGuid(), DateTime.UtcNow);

        evt.Provider.ShouldBe("stripe");
        evt.EventId.Length.ShouldBe(100);
        evt.ProcessedAt.ShouldBeNull();

        evt.Complete("Granted", DateTime.UtcNow);
        evt.Outcome.ShouldBe("Granted");
        evt.ProcessedAt.ShouldNotBeNull();
    }
}
