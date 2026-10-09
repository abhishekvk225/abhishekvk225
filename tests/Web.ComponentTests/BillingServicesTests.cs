using Microsoft.Extensions.Time.Testing;

namespace NexaVerify.Web.ComponentTests;

public class MoneyTests
{
    [Theory]
    [InlineData(0, "USD", "$0.00")]
    [InlineData(5, "USD", "$0.05")]
    [InlineData(4900, "USD", "$49.00")]
    [InlineData(123456789, "USD", "$1,234,567.89")]
    [InlineData(100, "EUR", "€1.00")]
    [InlineData(-250, "GBP", "-£2.50")]
    [InlineData(1500, "JPY", "¥1,500")]          // zero-decimal: minor units ARE yen
    [InlineData(0, "JPY", "¥0")]
    [InlineData(1000000, "KRW", "₩1,000,000")]
    [InlineData(12345, "KWD", "KWD 12.345")]     // three decimals
    [InlineData(5, "KWD", "KWD 0.005")]
    [InlineData(1, "BHD", "BHD 0.001")]
    [InlineData(1999, "CHF", "CHF 19.99")]       // no symbol known: the code is shown
    [InlineData(1999, "usd", "$19.99")]          // case does not matter
    [InlineData(1999, null, "19.99")]
    public void Minor_units_are_formatted_for_their_currency(long minor, string? currency, string expected) =>
        Money.Format(minor, currency).ShouldBe(expected);

    [Fact]
    public void The_largest_amount_formats_without_overflow_or_rounding()
    {
        Money.Format(long.MaxValue, "USD").ShouldBe("$92,233,720,368,547,758.07");
        Money.Format(long.MinValue, "USD").ShouldBe("-$92,233,720,368,547,758.08");
        Money.Format(long.MaxValue, "JPY").ShouldBe("¥9,223,372,036,854,775,807");
    }

    [Fact]
    public void Floating_point_trouble_amounts_stay_exact()
    {
        // 0.1 + 0.2 style traps: 10 + 20 cents is 30 cents, 1.15 * 100 is not 114.99999.
        Money.Format(10 + 20, "USD").ShouldBe("$0.30");
        Money.Format(115, "USD").ShouldBe("$1.15");
        Money.TryParseMajor("1.15", "USD").ShouldBe(115);
        Money.TryParseMajor("0.29", "USD").ShouldBe(29);
        Money.TryParseMajor("4.35", "USD").ShouldBe(435);
    }

    [Theory]
    [InlineData("49", "USD", 4900)]
    [InlineData("49.5", "USD", 4950)]
    [InlineData("49.50", "USD", 4950)]
    [InlineData("1,250.00", "USD", 125000)]
    [InlineData(" 12 ", "EUR", 1200)]
    [InlineData(".5", "USD", 50)]
    [InlineData("0", "USD", 0)]
    [InlineData("1500", "JPY", 1500)]
    [InlineData("12.345", "KWD", 12345)]
    [InlineData("12.3", "KWD", 12300)]
    public void A_typed_price_becomes_exact_minor_units(string input, string currency, long expected) =>
        Money.TryParseMajor(input, currency).ShouldBe(expected);

    [Theory]
    [InlineData("", "USD")]
    [InlineData("   ", "USD")]
    [InlineData("abc", "USD")]
    [InlineData("-5", "USD")]
    [InlineData("49.999", "USD")]      // more decimals than USD has: never rounded
    [InlineData("1.5", "JPY")]         // yen have no decimals
    [InlineData("12.3456", "KWD")]
    [InlineData("1.2.3", "USD")]
    [InlineData(".", "USD")]
    [InlineData("1e3", "USD")]
    [InlineData("$49", "USD")]
    [InlineData("92233720368547758.08", "USD")]    // one cent over the 64-bit limit
    [InlineData("99999999999999999999999999", "USD")]
    public void Anything_that_is_not_an_exact_amount_is_refused(string input, string currency) =>
        Money.TryParseMajor(input, currency).ShouldBeNull();

    [Theory]
    [InlineData("USD", 2)]
    [InlineData("JPY", 0)]
    [InlineData("KWD", 3)]
    [InlineData("zzz", 2)]
    public void Exponent_follows_the_currency(string currency, int expected) => Money.Exponent(currency).ShouldBe(expected);

    [Theory]
    [InlineData(4900, "USD", "49.00")]
    [InlineData(1500, "JPY", "1500")]
    [InlineData(12345, "KWD", "12.345")]
    public void Input_text_round_trips(long minor, string currency, string text)
    {
        Money.ToInputText(minor, currency).ShouldBe(text);
        Money.TryParseMajor(text, currency).ShouldBe(minor);
    }

    [Fact]
    public void Price_per_credit_is_compared_exactly()
    {
        Money.CheaperPerCredit(19900, 25000, 4900, 5000).ShouldBeTrue("0.796 vs 0.98 per credit");
        Money.CheaperPerCredit(4900, 5000, 19900, 25000).ShouldBeFalse();
        Money.CheaperPerCredit(100, 100, 200, 200).ShouldBeFalse("equal rates are not cheaper");
        Money.CheaperPerCredit(1, 0, 1, 1).ShouldBeFalse("a pack without credits is never best");
        Money.CheaperPerCredit(long.MaxValue, 1, long.MaxValue, 2).ShouldBeFalse("no overflow");
        Money.Difference(5880, 4900).ShouldBe(980);
    }
}

public class CheckoutRedirectTests
{
    private static readonly Uri Portal = new("http://localhost:5121/");

    [Theory]
    [InlineData("https://pay.example.test/session/abc")]
    [InlineData("https://checkout.stripe.com/c/pay/cs_test_1?x=y#frag")]
    [InlineData("HTTPS://PAY.EXAMPLE.TEST/a")]
    public void An_absolute_https_address_is_allowed_everywhere(string url)
    {
        CheckoutRedirect.Validate(url, allowDevPay: false).ShouldNotBeNull();
        CheckoutRedirect.Validate(url, allowDevPay: true, Portal).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("http://pay.example.test/a")]
    [InlineData("ftp://pay.example.test/a")]
    [InlineData("file:///etc/passwd")]
    [InlineData("//evil.example/a")]
    [InlineData("/dev/pay/00000000-0000-0000-0000-00000000d001")]     // relative: only in Development
    [InlineData("pay.example.test/a")]
    [InlineData("https://user:pass@pay.example.test/a")]
    [InlineData("https://")]
    [InlineData("https:\\\\evil.example")]
    [InlineData(" https://pay.example.test/a")]
    [InlineData("https://pay.example.test/a\r\nSet-Cookie: x=y")]
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_is_refused_outside_development(string? url) =>
        CheckoutRedirect.Validate(url, allowDevPay: false, Portal).ShouldBeNull();

    [Theory]
    [InlineData("/dev/pay/00000000-0000-0000-0000-00000000d001")]
    [InlineData("http://localhost:5121/dev/pay/00000000-0000-0000-0000-00000000d001")]
    public void In_development_the_portals_own_simulator_page_is_allowed(string url)
    {
        var target = CheckoutRedirect.Validate(url, allowDevPay: true, Portal);

        target.ShouldNotBeNull();
        target.Host.ShouldBe("localhost");
        target.AbsolutePath.ShouldStartWith("/dev/pay/");
    }

    [Theory]
    [InlineData("http://evil.example/dev/pay/1")]                  // another host
    [InlineData("http://localhost:9999/dev/pay/1")]                // another port
    [InlineData("http://localhost:5121/client/billing/buy")]       // not the simulator
    [InlineData("//localhost:5121/dev/pay/1")]
    [InlineData("javascript:/dev/pay/1")]
    [InlineData("http://localhost:5121@evil.example/dev/pay/1")]
    public void The_development_exception_is_narrow(string url) =>
        CheckoutRedirect.Validate(url, allowDevPay: true, Portal).ShouldBeNull();

    [Fact]
    public void The_simulator_path_is_not_a_way_around_production()
    {
        CheckoutRedirect.Validate("http://localhost:5121/dev/pay/1", allowDevPay: false, Portal).ShouldBeNull();
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Testing", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData("UiDemo", false)]
    public void Only_development_and_testing_may_use_the_simulator(string environment, bool allowed) =>
        CheckoutRedirect.IsDevHost(new FakeEnv(environment)).ShouldBe(allowed);
}

public class OrderPollerTests
{
    private readonly FakeTimeProvider _clock = new(BillingSample.Now);

    [Fact]
    public async Task Polling_backs_off_and_stops_at_the_first_settled_answer()
    {
        var answers = new Queue<ApiResult<OrderDto>>([
            ApiResult<OrderDto>.Ok(BillingSample.Order(OrderStatuses.Pending)),
            ApiResult<OrderDto>.Ok(BillingSample.Order(OrderStatuses.Pending)),
            ApiResult<OrderDto>.Ok(BillingSample.Order()),
        ]);
        var seen = new List<string>();
        var run = new OrderPoller(_clock).RunAsync(_ => Task.FromResult(answers.Dequeue()), r => { seen.Add(r.Value.Status); return Task.CompletedTask; }, default);

        await AdvanceUntilDoneAsync(run);

        (await run).TimedOut.ShouldBeFalse();
        seen.ShouldBe([OrderStatuses.Pending, OrderStatuses.Pending, OrderStatuses.Paid]);
        (_clock.GetUtcNow() - BillingSample.Now).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1 + 2), "1 s then 2 s between the three questions");
    }

    [Fact]
    public async Task A_payment_that_stays_pending_stops_after_about_a_minute_and_says_so()
    {
        var calls = 0;
        var run = new OrderPoller(_clock).RunAsync(_ => { calls++; return Task.FromResult(ApiResult<OrderDto>.Ok(BillingSample.Order(OrderStatuses.Pending))); }, _ => Task.CompletedTask, default);

        await AdvanceUntilDoneAsync(run);

        var outcome = await run;
        outcome.TimedOut.ShouldBeTrue();
        outcome.Last.Value.Status.ShouldBe(OrderStatuses.Pending);
        calls.ShouldBeInRange(10, 20, "a handful of questions, not a flood");
    }

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(401)]
    public async Task A_definite_error_ends_the_wait_at_once(int status)
    {
        var calls = 0;
        var outcome = await new OrderPoller(_clock).RunAsync(
            _ => { calls++; return Task.FromResult(ApiResult<OrderDto>.Fail("X", "no", null, status)); }, _ => Task.CompletedTask, default);

        calls.ShouldBe(1);
        outcome.TimedOut.ShouldBeFalse();
        outcome.Last.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task A_hiccup_is_retried_and_the_payment_is_still_found()
    {
        var calls = 0;
        var run = new OrderPoller(_clock).RunAsync(
            _ => Task.FromResult(++calls == 1 ? ApiResult<OrderDto>.Fail("API_UNAVAILABLE", "down", null, 503) : ApiResult<OrderDto>.Ok(BillingSample.Order())),
            _ => Task.CompletedTask, default);

        await AdvanceUntilDoneAsync(run);

        (await run).Last.Value.Status.ShouldBe(OrderStatuses.Paid);
        calls.ShouldBe(2);
    }

    [Fact]
    public async Task Cancelling_stops_the_wait_without_another_question()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var run = new OrderPoller(_clock).RunAsync(
            _ => { calls++; return Task.FromResult(ApiResult<OrderDto>.Ok(BillingSample.Order(OrderStatuses.Pending))); }, _ => Task.CompletedTask, cts.Token);
        await Task.Yield();

        cts.Cancel();
        _clock.Advance(TimeSpan.FromSeconds(10));

        await Should.ThrowAsync<OperationCanceledException>(() => run);
        calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 5)]
    [InlineData(40, 5)]
    public void The_delay_grows_then_levels_off(int attempt, int seconds) => OrderPoller.DelayFor(attempt).ShouldBe(TimeSpan.FromSeconds(seconds));

    private async Task AdvanceUntilDoneAsync(Task run)
    {
        for (var i = 0; i < 200 && !run.IsCompleted; i++)
        {
            await Task.Yield();
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

public class BillingFormTests
{
    [Fact]
    public void A_pack_form_turns_a_typed_price_into_exact_minor_units()
    {
        var form = new PackForm { Name = "Starter", Credits = 5000, ValidityDays = 365, PriceText = "49.5", Currency = "USD", HighlightsText = "One\n\n Two \n" };

        var request = form.ToRequest();

        request.PriceMinor.ShouldBe(4950);
        request.Currency.ShouldBe("USD");
        request.Highlights.ShouldBe(["One", "Two"]);
    }

    [Theory]
    [InlineData("USD", "49.999", "at most 2 decimals")]
    [InlineData("JPY", "1500.5", "whole number")]
    [InlineData("USD", "abc", "at most 2 decimals")]
    [InlineData("USD", "0", "more than zero")]
    [InlineData("KWD", "1.2345", "at most 3 decimals")]
    public void A_price_the_currency_cannot_hold_is_explained(string currency, string price, string expected)
    {
        var form = new PackForm { Name = "x", Currency = currency, PriceText = price };

        var messages = form.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(form)).Select(r => r.ErrorMessage).ToList();

        messages.ShouldContain(m => m!.Contains(expected));
    }

    [Fact]
    public void Editing_shows_the_price_in_major_units_and_keeps_the_row_version()
    {
        var form = PackForm.From(new AdminPackDto(Guid.NewGuid(), "Pilot", null, 100, 30, 1500, "JPY", ["a"], 2, true, false, "RV1"));

        form.PriceText.ShouldBe("1500");
        form.RowVersion.ShouldBe("RV1");
        form.ToRequest().PriceMinor.ShouldBe(1500);
    }

    [Fact]
    public void Too_many_highlights_are_refused()
    {
        var form = new PackForm { Name = "x", PriceText = "1", HighlightsText = string.Join('\n', Enumerable.Range(0, 9).Select(i => "h" + i)) };

        form.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(form)).ShouldContain(r => r.MemberNames.Contains("HighlightsText"));
    }

    [Fact]
    public void The_profile_hint_only_asks_for_what_an_invoice_needs()
    {
        BillingProfileForm.LooksIncomplete(null).ShouldBeTrue();
        BillingProfileForm.LooksIncomplete(BillingSample.EmptyProfile()).ShouldBeTrue();
        BillingProfileForm.LooksIncomplete(BillingSample.CompleteProfile() with { City = " " }).ShouldBeTrue();
        BillingProfileForm.LooksIncomplete(BillingSample.CompleteProfile() with { TaxId = null, AddressLine2 = null, State = null }).ShouldBeFalse("tax id, line 2 and state are optional");
    }

    [Fact]
    public void The_profile_request_is_tidied()
    {
        var form = BillingProfileForm.From(BillingSample.CompleteProfile());
        form.LegalName = "  Acme  ";
        form.Country = "gb";
        form.TaxId = "  ";

        var request = form.ToRequest();

        request.LegalName.ShouldBe("Acme");
        request.Country.ShouldBe("GB");
        request.TaxId.ShouldBeNull();
        request.RowVersion.ShouldBe("AAAAAAAB");
    }

    [Fact]
    public void A_checkout_attempt_keeps_its_key_until_it_is_reset_or_the_pack_changes()
    {
        var attempt = new CheckoutAttempt();
        var first = attempt.KeyFor(BillingSample.PackStarter);

        attempt.KeyFor(BillingSample.PackStarter).ShouldBe(first, "a retry or double click is the same purchase");
        attempt.KeyFor(BillingSample.PackGrowth).ShouldNotBe(first);
        attempt.Reset();
        attempt.KeyFor(BillingSample.PackStarter).ShouldNotBe(first);
    }

    [Theory]
    [InlineData(503, true)]
    [InlineData(403, true)]
    [InlineData(500, false)]
    [InlineData(422, false)]
    [InlineData(429, false)]
    public void Disabled_payments_are_recognised(int status, bool disabled) =>
        BillingErrors.IsDisabled(new ApiError("X", "m", null, status)).ShouldBe(disabled);

    [Fact]
    public void Order_status_helpers_agree_with_the_flow()
    {
        OrderStatuses.IsPaid(OrderStatuses.Paid).ShouldBeTrue();
        OrderStatuses.IsPaid(OrderStatuses.PartiallyRefunded).ShouldBeTrue();
        OrderStatuses.IsPaid(OrderStatuses.Pending).ShouldBeFalse();
        OrderStatuses.IsFailed(OrderStatuses.Expired).ShouldBeTrue();
        OrderStatuses.IsFailed(OrderStatuses.Refunded).ShouldBeFalse();
        OrderStatuses.All.Count.ShouldBe(7);
    }
}

public class LicenseHealthTrialTests
{
    [Theory]
    [InlineData(200, 200, 14.0, LicenseHealthLevel.Healthy)]     // a fresh trial: full balance, two weeks left
    [InlineData(200, 200, 7.0, LicenseHealthLevel.Healthy)]
    [InlineData(200, 200, 6.9, LicenseHealthLevel.Critical)]     // last week still warns
    [InlineData(199, 200, 14.0, LicenseHealthLevel.Warning)]     // as soon as anything is used, the time warning applies again
    [InlineData(150, 200, 20.0, LicenseHealthLevel.Warning)]
    [InlineData(60, 200, 90.0, LicenseHealthLevel.Warning)]
    [InlineData(10, 200, 90.0, LicenseHealthLevel.Critical)]
    [InlineData(0, 0, 14.0, LicenseHealthLevel.Critical)]        // nothing granted is not "untouched"
    public void A_full_balance_is_not_amber_just_because_the_license_is_young(long remaining, long total, double days, LicenseHealthLevel expected) =>
        LicenseHealth.Evaluate(remaining, total, days).ShouldBe(expected);
}
