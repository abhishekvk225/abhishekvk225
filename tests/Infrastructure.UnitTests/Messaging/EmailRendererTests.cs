using NexaVerify.Application.Messaging;
using NexaVerify.Domain.Messaging;
using NexaVerify.Infrastructure.Messaging;

namespace NexaVerify.Infrastructure.UnitTests.Messaging;

public class EmailRendererTests
{
    private const string Xss = "<script>alert(1)</script><img src=x onerror=alert(2)>\"'&";

    public static TheoryData<EmailTemplate> AllTemplates => new(Enum.GetValues<EmailTemplate>());

    [Theory]
    [MemberData(nameof(AllTemplates))]
    public void Every_template_renders_a_branded_multipart_message_without_remote_content(EmailTemplate template)
    {
        var rendered = EmailTestKit.Renderer().Render(EmailTestKit.Sample(template));

        rendered.Subject.ShouldNotBeNullOrWhiteSpace();
        rendered.Subject.ShouldNotContain("\r");
        rendered.Subject.ShouldNotContain("\n");
        rendered.TextBody.ShouldNotBeNullOrWhiteSpace();
        rendered.TextBody.ShouldNotContain("<");
        rendered.TextBody.ShouldContain("NexaVerify");

        var html = rendered.HtmlBody;
        html.ShouldStartWith("<!DOCTYPE html>");
        html.ShouldContain("lang=\"en\"");
        html.ShouldContain("#4F46E5"); // the NexaVerify indigo
        html.ShouldContain("style=\"");  // inline CSS
        html.ShouldContain("name=\"color-scheme\" content=\"light dark\"");
        html.ShouldContain("prefers-color-scheme: dark");
        html.ShouldContain("max-width: 620px"); // responsive
        html.ShouldNotContain("<img", Case.Insensitive);
        html.ShouldNotContain("src=", Case.Insensitive);
        html.ShouldNotContain("url(", Case.Insensitive);
        html.ShouldNotContain("@import", Case.Insensitive);
        html.ShouldNotContain("<link", Case.Insensitive);
        html.ShouldNotContain("<script", Case.Insensitive);
        html.ShouldNotContain("http://"); // no plain-http resource of any kind
        html.ShouldContain("NexaVerify Ltd, 1 Example Street, Bengaluru"); // the legal footer
    }

    [Theory]
    [MemberData(nameof(AllTemplates))]
    public void Hostile_values_never_reach_the_html_unescaped(EmailTemplate template)
    {
        var rendered = EmailTestKit.Renderer().Render(EmailTestKit.Sample(template, Xss));

        rendered.HtmlBody.ShouldNotContain("<script", Case.Insensitive);
        rendered.HtmlBody.ShouldNotContain("<img", Case.Insensitive);
        rendered.HtmlBody.ShouldNotContain("onerror=alert(2)>");
        rendered.HtmlBody.ShouldNotContain("alert(1)</script>");
        rendered.Subject.ShouldNotContain("\n");
    }

    [Fact]
    public void A_name_is_html_encoded_in_the_html_part_and_left_literal_in_the_text_part()
    {
        var rendered = EmailTestKit.Renderer().Render(EmailTestKit.Sample(EmailTemplate.PasswordReset, "<b>Ada</b> & \"Co\""));

        rendered.HtmlBody.ShouldContain("Hello &lt;b&gt;Ada&lt;/b&gt; &amp; &quot;Co&quot;,");
        rendered.TextBody.ShouldContain("Hello <b>Ada</b> & \"Co\",");
    }

    [Theory]
    [InlineData("Eve\r\nBcc: victim@example.com")]
    [InlineData("Eve\nSubject: pwned")]
    [InlineData("Eve\rX-Injected: 1")]
    [InlineData("Eve\u2028Bcc: victim@example.com")]
    [InlineData("Eve\u0000Bcc: victim@example.com")]
    public void Line_breaks_in_a_name_cannot_split_the_subject_or_add_headers(string name)
    {
        var request = EmailTestKit.Sample(EmailTemplate.ContactRequest, name);

        var rendered = EmailTestKit.Renderer().Render(request);

        rendered.Subject.ShouldStartWith("New contact request from Eve ");
        rendered.Subject.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029').ShouldBeFalse();
    }

    [Fact]
    public void A_very_long_value_cannot_make_the_subject_unbounded()
    {
        var rendered = EmailTestKit.Renderer().Render(EmailTestKit.Sample(EmailTemplate.ContactRequest, new string('x', 5000)));

        rendered.Subject.Length.ShouldBeLessThanOrEqualTo(EmailText.MaxSubjectLength);
    }

    [Fact]
    public void A_multi_line_message_keeps_its_line_breaks_but_is_still_escaped()
    {
        var request = EmailTestKit.Sample(EmailTemplate.ContactRequest);
        request.Block("message", "Line one\r\nLine <two>\rLine three\u0007");

        var rendered = EmailTestKit.Renderer().Render(request);

        rendered.TextBody.ShouldContain("Line one\nLine <two>\nLine three");
        rendered.TextBody.ShouldNotContain("\u0007");
        rendered.TextBody.ShouldNotContain("\r");
        rendered.HtmlBody.ShouldContain("Line one<br>Line &lt;two&gt;<br>Line three");
    }

    [Fact]
    public void The_verification_mail_has_exactly_one_link_line_with_the_token_and_the_agreed_wording()
    {
        var request = new EmailRequest(EmailTemplate.VerifyEmail, "ada@example.com", "Ada Lovelace")
            .Text("name", "Ada Lovelace").Text("company", "Acme Ltd").Number("trialCredits", 40).Number("trialDays", 10).Number("hours", 24)
            .Link("verify", "verify-email", ("email", "ada@example.com"), ("token", "abc_DEF-123"));

        var rendered = EmailTestKit.Renderer().Render(request);

        rendered.Subject.ShouldBe("Confirm your NexaVerify account");
        rendered.HasOneTimeToken.ShouldBeTrue();
        rendered.TextBody.ShouldContain("Hello Ada Lovelace,");
        rendered.TextBody.ShouldContain("40 credits for 10 days");
        rendered.TextBody.ShouldContain("24 hours");
        var link = rendered.TextBody.Split('\n').Single(l => l.StartsWith("https://", StringComparison.Ordinal));
        link.ShouldBe("https://portal.example.com/verify-email?email=ada%40example.com&token=abc_DEF-123");
        rendered.HtmlBody.ShouldContain("href=\"https://portal.example.com/verify-email?email=ada%40example.com&amp;token=abc_DEF-123\"");
    }

    [Theory]
    [InlineData(EmailTemplate.VerifyEmail, true)]
    [InlineData(EmailTemplate.PasswordReset, true)]
    [InlineData(EmailTemplate.UserInvite, true)]
    [InlineData(EmailTemplate.AlreadyRegistered, false)]
    [InlineData(EmailTemplate.PasswordChanged, false)]
    [InlineData(EmailTemplate.PaymentReceipt, false)]
    [InlineData(EmailTemplate.MfaRecoveryCodeUsed, false)]
    public void Only_mails_that_carry_a_one_time_token_are_flagged_for_early_erasure(EmailTemplate template, bool expected) =>
        EmailTestKit.Renderer().Render(EmailTestKit.Sample(template)).HasOneTimeToken.ShouldBe(expected);

    [Fact]
    public void An_optional_button_is_left_out_when_its_link_is_not_given()
    {
        var withoutLink = new EmailRequest(EmailTemplate.LicenseLowBalance, "a@b.test")
            .Text("company", "Acme").Text("licenseName", "Main").Number("remaining", 5).Number("total", 100).Number("percent", 5);
        var withLink = new EmailRequest(EmailTemplate.LicenseLowBalance, "a@b.test")
            .Text("company", "Acme").Text("licenseName", "Main").Number("remaining", 5).Number("total", 100).Number("percent", 5)
            .Link("buy", "client/billing/buy");

        var plain = EmailTestKit.Renderer().Render(withoutLink);
        var linked = EmailTestKit.Renderer().Render(withLink);

        plain.TextBody.ShouldNotContain("Buy more credits");
        plain.HtmlBody.ShouldNotContain("Buy more credits");
        linked.TextBody.ShouldContain("https://portal.example.com/client/billing/buy");
        linked.HtmlBody.ShouldContain("Buy more credits");
    }

    [Fact]
    public void A_required_link_or_variable_that_is_missing_is_a_programming_error()
    {
        var noLink = new EmailRequest(EmailTemplate.PasswordReset, "a@b.test").Text("name", "A").Number("minutes", 30);
        var noVariable = new EmailRequest(EmailTemplate.PasswordReset, "a@b.test").Number("minutes", 30).Link("action", "reset-password");

        Should.Throw<InvalidOperationException>(() => EmailTestKit.Renderer().Render(noLink));
        Should.Throw<InvalidOperationException>(() => EmailTestKit.Renderer().Render(noVariable)).Message.ShouldContain("name");
    }

    [Fact]
    public void A_link_outside_the_public_base_url_is_refused_by_the_renderer()
    {
        var request = EmailTestKit.Sample(EmailTemplate.PasswordReset);
        request.Link("action", "//evil.example.com/steal");

        Should.Throw<EmailLinkException>(() => EmailTestKit.Renderer().Render(request));
    }

    [Fact]
    public void The_footer_is_escaped_and_may_span_lines()
    {
        var rendered = EmailTestKit.Renderer("A&B <Ltd>\r\n1 Road, Town").Render(EmailTestKit.Sample(EmailTemplate.PasswordChanged));

        rendered.HtmlBody.ShouldContain("A&amp;B &lt;Ltd&gt;<br>1 Road, Town");
        rendered.TextBody.ShouldContain("A&B <Ltd>\n1 Road, Town");
    }

    [Fact]
    public void Invoice_and_pack_values_appear_in_the_receipt_in_both_parts()
    {
        var request = new EmailRequest(EmailTemplate.PaymentReceipt, "buyer@example.com")
            .Text("amount", "INR 1,180.00").Text("packName", "Growth <pack>").Number("credits", 1500).Text("validUntil", "1 January 2027")
            .Text("invoiceNumber", "INV-2026-000007").Link("invoice", "client/billing/orders/0a1b2c");

        var rendered = EmailTestKit.Renderer().Render(request);

        rendered.Subject.ShouldBe("Payment received - invoice INV-2026-000007");
        rendered.TextBody.ShouldContain("1,500 credits");
        rendered.TextBody.ShouldContain("INR 1,180.00");
        rendered.TextBody.ShouldContain("/client/billing/orders/0a1b2c");
        rendered.HtmlBody.ShouldContain("Growth &lt;pack&gt;");
    }
}
