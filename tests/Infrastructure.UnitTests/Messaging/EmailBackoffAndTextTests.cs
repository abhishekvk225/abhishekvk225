using NexaVerify.Infrastructure.Messaging;

namespace NexaVerify.Infrastructure.UnitTests.Messaging;

public class EmailBackoffTests
{
    private static EmailDispatcherOptions Options(int jitter = 0) => new() { BaseDelaySeconds = 30, MaxDelaySeconds = 3600, JitterPercent = jitter };

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    public void The_delay_doubles_with_each_attempt(int attempts, int seconds) =>
        EmailBackoff.Delay(attempts, Options(), 0.5).ShouldBe(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void The_delay_is_capped()
    {
        EmailBackoff.Delay(12, Options(), 0.5).ShouldBe(TimeSpan.FromSeconds(3600));
        EmailBackoff.Delay(1000, Options(20), 1.0).ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(3600));
    }

    [Fact]
    public void Jitter_spreads_the_delay_within_the_configured_percentage()
    {
        EmailBackoff.Delay(3, Options(20), 0.0).ShouldBe(TimeSpan.FromSeconds(96)); // 120 s - 20 %
        EmailBackoff.Delay(3, Options(20), 0.5).ShouldBe(TimeSpan.FromSeconds(120));
        EmailBackoff.Delay(3, Options(20), 1.0).ShouldBe(TimeSpan.FromSeconds(144)); // 120 s + 20 %
    }

    [Fact]
    public void Different_random_draws_give_different_delays_so_retries_do_not_arrive_together()
    {
        var delays = Enumerable.Range(0, 20).Select(i => EmailBackoff.Delay(4, Options(25), i / 20.0)).Distinct().Count();

        delays.ShouldBeGreaterThan(10);
    }

    [Fact]
    public void The_delay_is_never_below_one_second() =>
        EmailBackoff.Delay(1, new EmailDispatcherOptions { BaseDelaySeconds = 1, MaxDelaySeconds = 10, JitterPercent = 50 }, 0.0).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
}

public class EmailTextTests
{
    [Fact]
    public void Single_line_flattens_every_kind_of_line_break_and_control_character() =>
        EmailText.SingleLine("a\r\nb\nc\rd\te\u0000f\u2028g\u2029h\u202Ei").ShouldBe("a b c d e f g h i");

    [Fact]
    public void Single_line_collapses_runs_of_white_space_and_trims() =>
        EmailText.SingleLine("  a   b \r\n\r\n c  ").ShouldBe("a b c");

    [Fact]
    public void A_display_name_loses_quotes_and_angle_brackets()
    {
        EmailText.DisplayName("\"Ada\" <ada@evil.test>").ShouldBe("Ada ada@evil.test");
        EmailText.DisplayName("\r\n").ShouldBeNull();
        EmailText.DisplayName(new string('n', 500))!.Length.ShouldBe(100);
    }

    [Fact]
    public void An_error_text_is_one_line_bounded_and_free_of_secrets()
    {
        var text = EmailText.Error("535 login failed for user mailer with password S3cretPassw0rd!\r\nnext line", 300, ["S3cretPassw0rd!", "mailer", null, ""]);

        text.ShouldNotContain("S3cretPassw0rd!");
        text.ShouldNotContain("mailer");
        text.ShouldNotContain("\n");
        text.ShouldContain("***");
        EmailText.Error(new string('x', 1000), 300).Length.ShouldBe(300);
    }

    [Fact]
    public void Format_fills_placeholders_and_rejects_unknown_ones()
    {
        EmailText.Format("Hi {a}, {b}!", new Dictionary<string, string> { ["a"] = "x", ["b"] = "y" }).ShouldBe("Hi x, y!");
        Should.Throw<InvalidOperationException>(() => EmailText.Format("Hi {c}", new Dictionary<string, string>()));
        EmailText.Format("no placeholders {", new Dictionary<string, string>()).ShouldBe("no placeholders {");
    }
}
