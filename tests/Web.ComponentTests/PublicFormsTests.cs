using Microsoft.AspNetCore.Components.Web;
using NexaVerify.Web.Components.Marketing;
using NexaVerify.Web.Layouts;
using NexaVerify.Web.Pages.Marketing;

namespace NexaVerify.Web.ComponentTests;

public class SignupPageTests : PublicSiteTestBase
{
    private const string GoodPassword = "correct horse battery staple";

    private void FillValid(IRenderedComponent<Signup> cut, string email = "ada@acme.test", string password = GoodPassword, string confirm = GoodPassword, bool terms = true)
    {
        Fill(cut, "#signup-company", "  Acme Ltd ");
        Fill(cut, "#signup-name", "Ada Lovelace");
        Fill(cut, "#signup-email", email);
        Fill(cut, "#signup-password", password);
        Fill(cut, "#signup-confirm", confirm);
        if (terms)
        {
            cut.Find("#signup-terms").Change(true);
        }
    }

    [Fact]
    public void Empty_form_shows_inline_messages_and_calls_nothing()
    {
        var cut = Render<Signup>();

        cut.Find("form").Submit();

        var text = cut.Markup;
        text.ShouldContain("Enter your company name.");
        text.ShouldContain("Enter your full name.");
        text.ShouldContain("Enter your work email address.");
        text.ShouldContain("Choose a password.");
        text.ShouldContain("Please accept the terms to continue.");
        Api.Signups.ShouldBeEmpty();
    }

    [Fact]
    public void Short_password_and_mismatch_are_explained()
    {
        var cut = Render<Signup>();
        FillValid(cut, password: "short", confirm: "different");

        cut.Find("form").Submit();

        cut.Markup.ShouldContain("Use at least 12 characters.");
        cut.Markup.ShouldContain("The two passwords do not match.");
        cut.Find("#signup-password").GetAttribute("aria-invalid").ShouldBe("true");
        Api.Signups.ShouldBeEmpty();
    }

    [Fact]
    public void Invalid_email_is_refused_before_the_api_is_called()
    {
        var cut = Render<Signup>();
        FillValid(cut, email: "not-an-email");

        cut.Find("form").Submit();

        cut.Markup.ShouldContain("Enter a valid email address.");
        Api.Signups.ShouldBeEmpty();
    }

    [Fact]
    public void Successful_sign_up_sends_trimmed_values_and_shows_check_your_email_without_echoing_the_password()
    {
        var cut = Render<Signup>();
        FillValid(cut, email: "  ada@acme.test ");

        cut.Find("form").Submit();

        var sent = Api.Signups.ShouldHaveSingleItem();
        sent.CompanyName.ShouldBe("Acme Ltd");
        sent.Email.ShouldBe("ada@acme.test");
        sent.Password.ShouldBe(GoodPassword);
        sent.AcceptTerms.ShouldBeTrue();
        cut.Find("[data-testid=signup-sent] h2").TextContent.ShouldBe("Check your email");
        cut.FindAll("form").Count.ShouldBe(0);
        cut.Markup.ShouldNotContain(GoodPassword);
        cut.Find("[data-testid=resend]").GetAttribute("data-mk-delay").ShouldBe("60");
    }

    [Fact]
    public void The_confirmation_never_reveals_whether_the_address_is_already_registered()
    {
        // The API answers 202 either way; the page must look the same for any address and use only neutral wording.
        var first = Render<Signup>();
        FillValid(first, email: "new@acme.test");
        first.Find("form").Submit();
        var firstText = first.Find("[data-testid=signup-sent]").TextContent;

        var second = Render<Signup>();
        FillValid(second, email: "existing@acme.test");
        second.Find("form").Submit();
        var secondText = second.Find("[data-testid=signup-sent]").TextContent;

        firstText.Replace("new@acme.test", "X").ShouldBe(secondText.Replace("existing@acme.test", "X"));
        firstText.ToLowerInvariant().ShouldNotContain("already");
        firstText.ToLowerInvariant().ShouldNotContain("exists");
        firstText.ShouldContain("If we can create an account");
    }

    [Fact]
    public void Honeypot_filled_looks_like_success_but_sends_nothing()
    {
        var cut = Render<Signup>();
        FillValid(cut);
        Fill(cut, "#website", "http://spam.example");

        cut.Find("form").Submit();

        Api.Signups.ShouldBeEmpty();
        cut.FindAll("[data-testid=signup-sent]").Count.ShouldBe(1);
    }

    [Fact]
    public void Honeypot_is_hidden_from_people_and_assistive_technology()
    {
        var cut = Render<Signup>();

        var trap = cut.Find("[data-testid=honeypot]");
        trap.GetAttribute("aria-hidden").ShouldBe("true");
        trap.GetAttribute("class")!.ShouldContain("mk-hp");
        cut.Find("#website").GetAttribute("tabindex").ShouldBe("-1");
    }

    [Fact]
    public void Field_errors_from_the_api_appear_next_to_their_fields_and_the_password_is_cleared()
    {
        Api.Signup = _ => ApiResult<bool>.Fail(Problem(400, "Some of the details are not valid.", fields: new Dictionary<string, string[]> { ["password"] = ["This password is too common."] }));
        var cut = Render<Signup>();
        FillValid(cut);

        cut.Find("form").Submit();

        cut.Find("#signup-password-error").TextContent.ShouldContain("This password is too common.");
        cut.FindAll("[data-testid=signup-sent]").Count.ShouldBe(0);
        cut.Find("#signup-password").GetAttribute("value").ShouldBeNullOrEmpty();
        cut.Find("#signup-confirm").GetAttribute("value").ShouldBeNullOrEmpty();
    }

    [Fact]
    public void Rate_limited_and_server_errors_show_a_message_with_the_reference()
    {
        Api.Signup = _ => ApiResult<bool>.Fail(Problem(429, "Too many requests right now. Please wait a moment and try again."));
        var cut = Render<Signup>();
        FillValid(cut);

        cut.Find("form").Submit();

        cut.Find("[data-testid=signup-error]").TextContent.ShouldContain("Too many requests");
        cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-pub");
        cut.Find("[data-testid=signup-error]").GetAttribute("role").ShouldBe("alert");
        cut.FindAll("form").Count.ShouldBe(1, "the person can try again");
    }

    [Fact]
    public void Closed_sign_ups_say_so_and_offer_contact_instead_of_a_form()
    {
        Api.Config = () => ApiResult<PublicConfigDto>.Ok(new PublicConfigDto(false, 0, 0, new CaptchaConfigDto("none", null)));

        var cut = Render<Signup>();

        cut.Find("[data-testid=signup-closed] h2").TextContent.ShouldBe("Sign-ups are currently closed");
        cut.Find("[data-testid=closed-contact]").GetAttribute("href").ShouldBe("/contact");
        cut.FindAll("form").Count.ShouldBe(0);
    }

    [Fact]
    public void Config_failure_shows_an_error_with_retry_and_reference()
    {
        Api.Config = () => ApiResult<PublicConfigDto>.Fail(Problem(503, "The service is temporarily unavailable. Please try again shortly.", "corr-cfg"));

        var cut = Render<Signup>();

        cut.Find("[data-testid=signup-config-error]").TextContent.ShouldContain("temporarily unavailable");
        cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-cfg");
        cut.Find("[data-testid=retry]").GetAttribute("href").ShouldBe("/signup");
        cut.FindAll("form").Count.ShouldBe(0);
    }

    [Fact]
    public void Turnstile_widget_appears_only_when_the_api_asks_for_it_and_a_missing_token_blocks_submit()
    {
        Render<Signup>().FindAll("[data-testid=turnstile]").Count.ShouldBe(0);

        Api.Config = () => ApiResult<PublicConfigDto>.Ok(new PublicConfigDto(true, 200, 14, new CaptchaConfigDto("turnstile", "site-key-1")));
        var cut = Render<Signup>();

        cut.Find(".cf-turnstile").GetAttribute("data-sitekey").ShouldBe("site-key-1");
        FillValid(cut);
        cut.Find("form").Submit();

        cut.Markup.ShouldContain("Please complete the security check");
        Api.Signups.ShouldBeEmpty();
    }

    [Fact]
    public void A_none_provider_never_loads_a_captcha_script()
    {
        Render<Signup>().Markup.ShouldNotContain("challenges.cloudflare.com");
    }

    [Fact]
    public void Password_policy_hint_and_strength_meter_are_present()
    {
        var cut = Render<Signup>();

        cut.Find("#signup-password-hint").TextContent.ShouldContain("At least 12 characters");
        cut.FindAll("[data-testid=password-strength]").Count.ShouldBe(1);
        cut.Find("[data-mk-strength-for=signup-password]").ShouldNotBeNull();
        cut.Find("#signup-password").GetAttribute("autocomplete").ShouldBe("new-password");
        cut.Find("#signup-password").GetAttribute("type").ShouldBe("password");
    }

    [Fact]
    public void Terms_label_mentions_consent_of_the_people_registered()
    {
        Render<Signup>().Find("label[for=signup-terms]").TextContent.ShouldContain("consent that the law requires");
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("short", 1)]
    [InlineData("abcdefghijkl", 2)]
    [InlineData("correct horse battery staple", 4)]
    public void Strength_score_is_advisory_and_monotonic(string password, int expected) => PasswordStrength.Score(password).ShouldBe(expected);
}

public class ContactPageTests : PublicSiteTestBase
{
    private void FillValid(IRenderedComponent<Contact> cut)
    {
        Fill(cut, "#contact-name", "Ada");
        Fill(cut, "#contact-email", "ada@acme.test");
        Fill(cut, "#contact-company", "Acme");
        Fill(cut, "#contact-message", "We would like to talk about volumes.");
    }

    [Fact]
    public void Empty_form_shows_messages_and_calls_nothing()
    {
        var cut = Render<Contact>();

        cut.Find("form").Submit();

        cut.Markup.ShouldContain("Enter your name.");
        cut.Markup.ShouldContain("Enter your email address.");
        cut.Markup.ShouldContain("Tell us how we can help.");
        Api.Contacts.ShouldBeEmpty();
    }

    [Fact]
    public void Too_short_message_is_refused()
    {
        var cut = Render<Contact>();
        FillValid(cut);
        Fill(cut, "#contact-message", "Hi");

        cut.Find("form").Submit();

        cut.Markup.ShouldContain("Please write between 10 and 4000 characters.");
        Api.Contacts.ShouldBeEmpty();
    }

    [Fact]
    public void Valid_message_is_sent_and_thanks_are_shown()
    {
        var cut = Render<Contact>();
        FillValid(cut);

        cut.Find("form").Submit();

        var sent = Api.Contacts.ShouldHaveSingleItem();
        sent.Name.ShouldBe("Ada");
        sent.Company.ShouldBe("Acme");
        cut.Find("[data-testid=contact-sent]").TextContent.ShouldContain("Thank you");
        cut.FindAll("form").Count.ShouldBe(0);
    }

    [Fact]
    public void Honeypot_filled_sends_nothing()
    {
        var cut = Render<Contact>();
        FillValid(cut);
        Fill(cut, "#website", "spam");

        cut.Find("form").Submit();

        Api.Contacts.ShouldBeEmpty();
        cut.FindAll("[data-testid=contact-sent]").Count.ShouldBe(1);
    }

    [Fact]
    public void Failures_keep_the_form_and_show_a_reference()
    {
        Api.Contact = _ => ApiResult<bool>.Fail(Problem(429, "Too many requests right now. Please wait a moment and try again."));
        var cut = Render<Contact>();
        FillValid(cut);

        cut.Find("form").Submit();

        cut.Find("[data-testid=contact-error]").TextContent.ShouldContain("Too many requests");
        cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-pub");
        cut.Find("#contact-message").GetAttribute("value")!.ShouldContain("volumes", Case.Sensitive, "the message is kept so nothing is lost");
    }

    [Fact]
    public void Api_field_errors_are_shown_next_to_the_field()
    {
        Api.Contact = _ => ApiResult<bool>.Fail(Problem(400, fields: new Dictionary<string, string[]> { ["email"] = ["That address is not accepted."] }));
        var cut = Render<Contact>();
        FillValid(cut);

        cut.Find("form").Submit();

        cut.Find("#contact-email-error").TextContent.ShouldContain("That address is not accepted.");
    }
}

public class VerifyEmailPageTests : PublicSiteTestBase
{
    private IRenderedComponent<VerifyEmail> RenderWith(string? email, string? token)
    {
        var query = new List<string>();
        if (email is not null)
        {
            query.Add("email=" + Uri.EscapeDataString(email));
        }

        if (token is not null)
        {
            query.Add("token=" + Uri.EscapeDataString(token));
        }

        Services.GetRequiredService<NavigationManager>().NavigateTo("/verify-email" + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty));
        return Render<VerifyEmail>();
    }

    [Fact]
    public void A_valid_link_asks_for_a_deliberate_confirmation_and_does_not_call_the_api_on_load()
    {
        var cut = RenderWith("ada@acme.test", "tok-123");

        cut.Find("[data-testid=verify-submit]").TextContent.ShouldBe("Confirm my email");
        cut.Markup.ShouldContain("ada@acme.test");
        cut.Markup.ShouldNotContain("tok-123", Case.Sensitive, "the token is never shown");
        Api.Verifications.ShouldBeEmpty();
    }

    [Fact]
    public void Confirming_calls_the_api_and_offers_sign_in()
    {
        var cut = RenderWith("ada@acme.test", "tok-123");

        cut.Find("form").Submit();

        var call = Api.Verifications.ShouldHaveSingleItem();
        call.Email.ShouldBe("ada@acme.test");
        call.Token.ShouldBe("tok-123");
        cut.Find("[data-testid=verify-signin]").GetAttribute("href").ShouldBe("/login");
        cut.Find("h1").TextContent.ShouldBe("Email confirmed");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("ada@acme.test", null)]
    [InlineData(null, "tok")]
    [InlineData("", "tok")]
    public void Incomplete_links_fail_generically_with_a_way_to_restart(string? email, string? token)
    {
        var cut = RenderWith(email, token);

        cut.Find("[data-testid=verify-invalid]").TextContent.ShouldContain("not valid any more");
        cut.Find("[data-testid=verify-restart]").GetAttribute("href").ShouldBe("/signup");
        Api.Verifications.ShouldBeEmpty();
    }

    [Fact]
    public void A_rejected_token_gives_the_same_generic_failure()
    {
        Api.Verify = _ => ApiResult<bool>.Fail(Problem(400, "Some of the details are not valid."));
        var cut = RenderWith("ada@acme.test", "stale");

        cut.Find("form").Submit();

        var text = cut.Find("[data-testid=verify-invalid]").TextContent;
        text.ShouldContain("expired or already been used");
        text.ShouldNotContain("stale");
        cut.Find("[data-testid=verify-restart]").ShouldNotBeNull();
    }

    [Fact]
    public void A_temporary_failure_keeps_the_button_and_shows_the_reference()
    {
        Api.Verify = _ => ApiResult<bool>.Fail(Problem(503, "The service is temporarily unavailable. Please try again shortly.", "corr-v"));
        var cut = RenderWith("ada@acme.test", "tok-123");

        cut.Find("form").Submit();

        cut.Find("[data-testid=verify-error]").TextContent.ShouldContain("temporarily unavailable");
        cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-v");
        cut.FindAll("[data-testid=verify-submit]").Count.ShouldBe(1);
    }
}

public class PricingPageTests : PublicSiteTestBase
{
    [Fact]
    public void Plans_are_listed_with_contact_us_where_there_is_no_price()
    {
        var cut = Render<Pricing>();

        var cards = cut.FindAll("[data-testid=plan-card]");
        cards.Count.ShouldBe(2);
        cards[0].QuerySelector("[data-testid=plan-price]")!.TextContent.ShouldBe("Free");
        cards[0].TextContent.ShouldContain("200 credits");
        cards[0].TextContent.ShouldContain("14 days");
        cards[0].QuerySelector("a")!.GetAttribute("href").ShouldBe("/signup");
        cards[1].QuerySelector("[data-testid=plan-price]")!.TextContent.ShouldBe("Contact us");
        cards[1].TextContent.ShouldContain("50,000 credits");
        cards[1].TextContent.ShouldContain("1 year");
        cards[1].QuerySelector("a")!.GetAttribute("href").ShouldBe("/contact");
        cut.FindAll("[data-testid=faq] details").Count.ShouldBeGreaterThan(2);
    }

    [Fact]
    public void Errors_show_retry_and_the_correlation_id()
    {
        Api.Plans = () => ApiResult<IReadOnlyList<PublicPlanDto>>.Fail(Problem(500, correlation: "corr-plans"));

        var cut = Render<Pricing>();

        cut.Find("[data-testid=plans-error]").GetAttribute("role").ShouldBe("alert");
        cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-plans");
        cut.Find("[data-testid=retry]").GetAttribute("href").ShouldBe("/pricing");
        cut.FindAll("[data-testid=plan-card]").Count.ShouldBe(0);
    }

    [Fact]
    public void No_plans_shows_an_empty_state_with_contact()
    {
        Api.Plans = () => ApiResult<IReadOnlyList<PublicPlanDto>>.Ok([]);

        var cut = Render<Pricing>();

        cut.FindAll("[data-testid=plans-empty]").Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(1, "1 day")]
    [InlineData(14, "14 days")]
    [InlineData(365, "1 year")]
    [InlineData(730, "2 years")]
    public void Validity_is_written_in_plain_words(int days, string text) => PlanCard.ValidityText(days).ShouldBe(text);
}
