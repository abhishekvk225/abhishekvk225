using NexaVerify.Infrastructure.Messaging;

namespace NexaVerify.Infrastructure.UnitTests.Messaging;

public class EmailOptionsValidatorTests
{
    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(EmailOptions options, string environment = "Production") =>
        new EmailOptionsValidator(new StubEnvironment(environment)).Validate(null, options);

    private static EmailOptions Smtp(Action<EmailOptions>? tweak = null)
    {
        var options = new EmailOptions
        {
            Provider = "Smtp",
            From = { Name = "NexaVerify", Address = "no-reply@example.com" },
            Smtp = { Host = "smtp.example.com", Port = 587, Username = "user", Password = "secret" },
        };
        tweak?.Invoke(options);
        return options;
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("UAT")]
    public void The_logging_provider_is_refused_outside_development_and_testing(string environment)
    {
        var result = Validate(new EmailOptions(), environment);

        result.Failed.ShouldBeTrue();
        result.FailureMessage!.ShouldContain("Logging email provider");
        result.FailureMessage!.ShouldContain(environment);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("testing")]
    public void The_logging_provider_is_fine_in_development_and_testing(string environment) =>
        Validate(new EmailOptions(), environment).Succeeded.ShouldBeTrue();

    [Fact]
    public void The_logging_provider_can_be_allowed_on_purpose_elsewhere() =>
        Validate(new EmailOptions { AllowLoggingProvider = true }).Succeeded.ShouldBeTrue();

    [Fact]
    public void A_complete_smtp_configuration_is_valid_in_production() =>
        Validate(Smtp()).Succeeded.ShouldBeTrue();

    [Fact]
    public void An_unknown_provider_is_refused() =>
        Validate(new EmailOptions { Provider = "Pigeon", AllowLoggingProvider = true }, "Development").FailureMessage!.ShouldContain("Email:Provider");

    [Fact]
    public void Smtp_needs_a_host() =>
        Validate(Smtp(o => o.Smtp.Host = " ")).FailureMessage!.ShouldContain("Email:Smtp:Host");

    [Fact]
    public void A_username_without_a_password_is_refused() =>
        Validate(Smtp(o => o.Smtp.Password = null)).FailureMessage!.ShouldContain("together");

    [Fact]
    public void An_anonymous_relay_is_allowed() =>
        Validate(Smtp(o => { o.Smtp.Username = null; o.Smtp.Password = null; })).Succeeded.ShouldBeTrue();

    [Fact]
    public void Plaintext_smtp_to_a_remote_host_needs_an_explicit_opt_in()
    {
        Validate(Smtp(o => o.Smtp.Security = SmtpSecurity.None)).FailureMessage!.ShouldContain("unencrypted");
        Validate(Smtp(o => { o.Smtp.Security = SmtpSecurity.None; o.Smtp.AllowPlaintext = true; })).Succeeded.ShouldBeTrue();
        Validate(Smtp(o => { o.Smtp.Security = SmtpSecurity.None; o.Smtp.Host = "127.0.0.1"; })).Succeeded.ShouldBeTrue();
        Validate(Smtp(o => { o.Smtp.Security = SmtpSecurity.None; o.Smtp.Host = "localhost"; })).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("no-reply@example.com", true)]
    [InlineData("NexaVerify <no-reply@example.com>", false)]
    [InlineData("no-reply", false)]
    [InlineData("no-reply@localhost", false)]
    [InlineData("a@b.com\r\nBcc: x@y.com", false)]
    [InlineData("a b@example.com", false)]
    [InlineData("", false)]
    public void The_from_address_must_be_a_plain_address(string address, bool valid) =>
        Validate(Smtp(o => o.From.Address = address)).Succeeded.ShouldBe(valid);

    [Fact]
    public void The_reply_to_address_is_checked_when_given()
    {
        Validate(Smtp(o => o.ReplyTo = "support@example.com")).Succeeded.ShouldBeTrue();
        Validate(Smtp(o => o.ReplyTo = "Support <support@example.com>")).FailureMessage!.ShouldContain("Email:ReplyTo");
    }

    [Fact]
    public void The_public_base_url_must_be_https_without_a_query()
    {
        Validate(Smtp(o => o.PublicBaseUrl = "https://portal.example.com")).Succeeded.ShouldBeTrue();
        Validate(Smtp(o => o.PublicBaseUrl = "http://portal.example.com")).FailureMessage!.ShouldContain("Email:PublicBaseUrl");
        Validate(Smtp(o => o.PublicBaseUrl = "https://portal.example.com/?a=b")).FailureMessage!.ShouldContain("Email:PublicBaseUrl");
    }

    [Fact]
    public void Out_of_range_numbers_are_refused()
    {
        Validate(Smtp(o => o.Smtp.TimeoutSeconds = 0)).Failed.ShouldBeTrue();
        Validate(Smtp(o => o.Smtp.Port = 70000)).Failed.ShouldBeTrue();
        Validate(Smtp(o => o.MaxPerMinute = 0)).Failed.ShouldBeTrue();
        Validate(Smtp(o => o.Dispatcher.Parallelism = 0)).Failed.ShouldBeTrue();
        Validate(Smtp(o => o.Dispatcher.MaxAttempts = 0)).Failed.ShouldBeTrue();
        Validate(Smtp(o => { o.Dispatcher.BaseDelaySeconds = 100; o.Dispatcher.MaxDelaySeconds = 50; })).Failed.ShouldBeTrue();
    }

    [Fact]
    public void A_lease_shorter_than_the_worst_case_cycle_is_refused_and_zero_derives_it()
    {
        var options = Smtp(o => { o.Dispatcher.BatchSize = 20; o.Dispatcher.Parallelism = 4; o.Smtp.TimeoutSeconds = 15; });
        var derived = EmailOptionsValidator.DerivedLease(options);

        derived.ShouldBe(TimeSpan.FromSeconds((5 * ((15 * 4) + 5)) + 30));
        EmailOptionsValidator.ResolveLease(options).ShouldBe(derived);
        Validate(Smtp(o => { o.Dispatcher.LeaseSeconds = 10; })).FailureMessage!.ShouldContain("LeaseSeconds");
        Validate(Smtp(o => o.Dispatcher.LeaseSeconds = 3000)).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void A_missing_ca_file_is_refused() =>
        Validate(Smtp(o => o.Smtp.CaCertificateFile = "/nonexistent/ca.pem")).FailureMessage!.ShouldContain("CaCertificateFile");

    [Fact]
    public void The_failure_message_never_contains_the_password()
    {
        var result = Validate(Smtp(o => { o.Smtp.Host = " "; o.Smtp.Password = "TopSecretPassw0rd"; }));

        result.Failed.ShouldBeTrue();
        result.FailureMessage!.ShouldNotContain("TopSecretPassw0rd");
    }
}
