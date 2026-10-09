using Microsoft.Extensions.Options;
using NexaVerify.Application.Identity;
using NexaVerify.Application.Persistence;
using NexaVerify.Application.Public;
using NexaVerify.Application.Tenancy;
using NexaVerify.Contracts.Public;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.UnitTests;

public class PublicSignupRulesTests
{
    private static readonly PasswordPolicy Policy = new(Options.Create(new PasswordPolicyOptions()));

    private static DisposableEmailPolicy Disposable(params string[] extra) =>
        new(Options.Create(new SignupOptions { DisposableEmailDomains = extra }));

    private static SignupRequest Valid() => new("Acme Ltd", "Ada Lovelace", "ada@acme.test", "Correct-Horse-Battery-9", true);

    // ---- client codes ----

    [Theory]
    [InlineData("Acme Ltd", "ACME-LTD")]
    [InlineData("  Zoë's Café & Co.  ", "ZOES-CAFE-CO")]
    [InlineData("ACME", "ACME")]
    [InlineData("日本語", "CLIENT")]
    [InlineData("", "CLIENT")]
    [InlineData("---", "CLIENT")]
    public void Client_codes_are_derived_from_the_company_name(string name, string expected)
    {
        ClientCodes.BaseFrom(name).ShouldBe(expected);
    }

    [Fact]
    public void Client_codes_fit_the_domain_rule_even_with_a_suffix()
    {
        var code = ClientCodes.WithRandomSuffix(ClientCodes.BaseFrom(new string('a', 100) + " b c"));

        code.Length.ShouldBeLessThanOrEqualTo(30);
        code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-').ShouldBeTrue();
        NexaVerify.Domain.Tenancy.Client.Create(code, "n", "a@b.test", "UTC", DateTime.UtcNow).Code.ShouldBe(code);
        ClientCodes.WithRandomSuffix("ACME").ShouldNotBe(ClientCodes.WithRandomSuffix("ACME")); // random
    }

    // ---- disposable domains ----

    [Theory]
    [InlineData("x@mailinator.com", true)]
    [InlineData("x@MAILINATOR.COM", true)]
    [InlineData("x@sub.mailinator.com", true)]
    [InlineData("x@acme.test", false)]
    [InlineData("x@notmailinator.com", false)]
    [InlineData("not-an-email", false)]
    public void The_built_in_blocklist_matches_domains_and_subdomains(string email, bool blocked)
    {
        Disposable().IsDisposable(email).ShouldBe(blocked);
    }

    [Fact]
    public void Configured_domains_extend_the_blocklist()
    {
        Disposable("@Burner.example", ".junk.test").IsDisposable("a@burner.example").ShouldBeTrue();
        Disposable("@Burner.example", ".junk.test").IsDisposable("a@x.junk.test").ShouldBeTrue();
        Disposable().IsDisposable("a@burner.example").ShouldBeFalse();
    }

    // ---- validators ----

    [Fact]
    public async Task A_complete_signup_is_valid_and_surrounding_whitespace_in_the_email_is_tolerated()
    {
        var validator = new SignupRequestValidator(Policy, Disposable());

        (await validator.ValidateAsync(Valid())).IsValid.ShouldBeTrue();
        (await validator.ValidateAsync(Valid() with { Email = "  ada@acme.test " })).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("CompanyName", "")]
    [InlineData("FullName", " ")]
    [InlineData("Email", "not-an-email")]
    [InlineData("Email", "ada@localhost")]
    [InlineData("Email", "x@mailinator.com")]
    [InlineData("Password", "short")]
    [InlineData("Password", "password1234")]
    public async Task Invalid_fields_are_reported_against_their_own_property(string property, string value)
    {
        var request = property switch
        {
            "CompanyName" => Valid() with { CompanyName = value },
            "FullName" => Valid() with { FullName = value },
            "Email" => Valid() with { Email = value },
            _ => Valid() with { Password = value },
        };

        var result = await new SignupRequestValidator(Policy, Disposable()).ValidateAsync(request);

        result.Errors.Select(e => e.PropertyName).ShouldContain(property);
    }

    [Fact]
    public async Task The_terms_must_be_accepted_and_the_password_must_not_contain_the_email_name()
    {
        var validator = new SignupRequestValidator(Policy, Disposable());

        (await validator.ValidateAsync(Valid() with { AcceptTerms = false })).Errors.Select(e => e.PropertyName).ShouldContain("AcceptTerms");
        (await validator.ValidateAsync(Valid() with { Password = "my-ada@acme.test-pass", Email = "ada@acme.test" })).IsValid.ShouldBeTrue(); // "ada" is < 4 chars: allowed by the shared policy
        (await validator.ValidateAsync(Valid() with { Email = "lovelace@acme.test", Password = "lovelace-is-my-name" })).Errors.Select(e => e.PropertyName).ShouldContain("Password");
    }

    [Fact]
    public async Task Verify_and_contact_requests_are_validated()
    {
        var verify = new VerifySignupRequestValidator();
        (await verify.ValidateAsync(new VerifySignupRequest("ada@acme.test", "t"))).IsValid.ShouldBeTrue();
        (await verify.ValidateAsync(new VerifySignupRequest("nope", ""))).Errors.Count.ShouldBe(2);

        var contact = new SubmitContactRequestValidator();
        (await contact.ValidateAsync(new SubmitContactRequest("Ada", "ada@acme.test", null, "Hi"))).IsValid.ShouldBeTrue();
        (await contact.ValidateAsync(new SubmitContactRequest("", "bad", new string('c', 151), new string('m', 4001)))).Errors.Count.ShouldBe(4);
    }

    // ---- options ----

    [Fact]
    public void Turnstile_needs_both_keys_and_an_https_verify_url()
    {
        var validator = new CaptchaOptionsValidator();

        validator.Validate(null, new CaptchaOptions()).Succeeded.ShouldBeTrue();
        validator.Validate(null, new CaptchaOptions { Provider = "turnstile" }).Failed.ShouldBeTrue();
        validator.Validate(null, new CaptchaOptions { Provider = "turnstile", SiteKey = "s", SecretKey = "k" }).Succeeded.ShouldBeTrue();
        validator.Validate(null, new CaptchaOptions { Provider = "turnstile", SiteKey = "s", SecretKey = "k", VerifyUrl = "http://x.test" }).Failed.ShouldBeTrue();
        validator.Validate(null, new CaptchaOptions { Provider = "recaptcha" }).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("https://portal.example.com", true)]
    [InlineData("https://portal.example.com/", true)]
    [InlineData("http://localhost:5102", true)]
    [InlineData("http://portal.example.com", false)]
    [InlineData("https://portal.example.com/?x=1", false)]
    [InlineData("portal.example.com", false)]
    public void The_portal_base_url_must_be_https_except_for_localhost(string url, bool valid)
    {
        new PortalLinksOptionsValidator().Validate(null, new PortalLinksOptions { PublicBaseUrl = url }).Succeeded.ShouldBe(valid);
    }

    [Fact]
    public void Portal_links_are_built_from_the_configured_base_and_escape_values()
    {
        var options = new PortalLinksOptions { PublicBaseUrl = "https://portal.example.com/" };

        options.LinkTo("verify-email", ("email", "a+b@x.test"), ("token", "t/k=")).ShouldBe("https://portal.example.com/verify-email?email=a%2Bb%40x.test&token=t%2Fk%3D");
        options.LinkTo("login").ShouldBe("https://portal.example.com/login");
    }

    // ---- public info ----

    private sealed class PlansFake : IPlanRepository
    {
        public List<Plan> Public { get; } = [];

        public Task<IReadOnlyList<Plan>> ListPublicAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Plan>>(Public);

        public Task<IReadOnlyList<Plan>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Plan?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> CodeExistsAsync(string normalizedCode, Guid? exceptId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Plan?> GetTrialAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Add(Plan plan) => throw new NotSupportedException();
    }

    [Fact]
    public async Task The_trial_plan_shows_the_configured_trial_values_and_other_plans_their_own()
    {
        var plans = new PlansFake();
        var trial = Plan.Create("TRIAL", "Free trial", 500, 30);
        trial.IsTrial = true;
        trial.Highlights = ["One", "Two"];
        var paid = Plan.Create("PRO", "Pro", 5000, 365);
        paid.DisplayPrice = "From 99 / month";
        plans.Public.AddRange([trial, paid]);
        var service = new PublicInfoService(plans, Options.Create(new SignupOptions { TrialCredits = 75, TrialDays = 7 }), Options.Create(new CaptchaOptions()));

        var result = (await service.GetPlansAsync(default)).Value;

        result[0].ShouldSatisfyAllConditions(
            p => p.IsTrial.ShouldBeTrue(),
            p => p.Credits.ShouldBe(75),
            p => p.ValidityDays.ShouldBe(7),
            p => p.Highlights.ShouldBe(["One", "Two"]));
        result[1].ShouldSatisfyAllConditions(
            p => p.IsTrial.ShouldBeFalse(),
            p => p.Credits.ShouldBe(5000),
            p => p.ValidityDays.ShouldBe(365),
            p => p.DisplayPrice.ShouldBe("From 99 / month"));
    }

    [Fact]
    public void Config_reports_the_switches_and_exposes_the_site_key_only_when_a_provider_is_on()
    {
        var off = new PublicInfoService(new PlansFake(), Options.Create(new SignupOptions { Enabled = false, TrialCredits = 10, TrialDays = 3 }),
            Options.Create(new CaptchaOptions { SiteKey = "ignored", SecretKey = "never-exposed" })).GetConfig();
        var on = new PublicInfoService(new PlansFake(), Options.Create(new SignupOptions()),
            Options.Create(new CaptchaOptions { Provider = "turnstile", SiteKey = "site-key", SecretKey = "never-exposed" })).GetConfig();

        off.ShouldBe(new PublicConfigDto(false, 10, 3, new CaptchaConfigDto("none", null)));
        on.Captcha.ShouldBe(new CaptchaConfigDto("turnstile", "site-key"));
        System.Text.Json.JsonSerializer.Serialize(on).ShouldNotContain("never-exposed");
    }
}
