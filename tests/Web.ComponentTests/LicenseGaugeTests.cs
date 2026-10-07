namespace NexaVerify.Web.ComponentTests;

public class LicenseGaugeTests : UiTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);

    // Rules: red = <10 % left OR <7 days; amber = 10..30 % inclusive OR 7..30 days inclusive; green otherwise (>30 % and >30 days / no expiry).
    [Theory]
    [InlineData(1000, 1000, null, LicenseHealthLevel.Healthy)]
    [InlineData(310, 1000, null, LicenseHealthLevel.Healthy)]
    [InlineData(301, 1000, 31.0, LicenseHealthLevel.Healthy)]
    [InlineData(300, 1000, null, LicenseHealthLevel.Warning)]   // exactly 30 % is amber
    [InlineData(100, 1000, null, LicenseHealthLevel.Warning)]   // exactly 10 % is amber
    [InlineData(99, 1000, null, LicenseHealthLevel.Critical)]   // < 10 %
    [InlineData(0, 1000, null, LicenseHealthLevel.Critical)]
    [InlineData(900, 1000, 30.0, LicenseHealthLevel.Warning)]   // exactly 30 days is amber
    [InlineData(900, 1000, 30.5, LicenseHealthLevel.Healthy)]
    [InlineData(900, 1000, 7.0, LicenseHealthLevel.Warning)]    // exactly 7 days is amber
    [InlineData(900, 1000, 6.99, LicenseHealthLevel.Critical)]  // < 7 days
    [InlineData(900, 1000, 0.0, LicenseHealthLevel.Critical)]
    [InlineData(900, 1000, -3.0, LicenseHealthLevel.Critical)]  // expired
    [InlineData(250, 1000, 3.0, LicenseHealthLevel.Critical)]   // worst of both wins
    [InlineData(0, 0, null, LicenseHealthLevel.Critical)]       // nothing granted
    [InlineData(2000, 1000, null, LicenseHealthLevel.Healthy)]  // clamped
    public void Evaluate_applies_thresholds(long remaining, long total, double? days, LicenseHealthLevel expected) =>
        LicenseHealth.Evaluate(remaining, total, days).ShouldBe(expected);

    [Fact]
    public void Renders_plain_language_summary_and_amber_when_expiry_is_within_30_days()
    {
        var cut = Render<LicenseGauge>(p => p
            .Add(x => x.Remaining, 842).Add(x => x.Total, 1000)
            .Add(x => x.ExpiresAt, Now.AddDays(23).AddHours(2)).Add(x => x.Now, Now));

        cut.Find("[data-testid=gauge-summary]").TextContent.ShouldBe("842 of 1,000 credits left · expires in 23 days");
        cut.Find("[data-testid=license-gauge]").GetAttribute("data-level").ShouldBe("Warning");
        cut.Find("[data-testid=gauge-percent]").TextContent.ShouldBe("84%");
    }

    [Theory]
    [InlineData(800, 90, "Healthy")]
    [InlineData(200, 90, "Warning")]
    [InlineData(50, 90, "Critical")]
    [InlineData(800, 3, "Critical")]
    public void Renders_level_attribute(long remaining, int days, string level)
    {
        var cut = Render<LicenseGauge>(p => p
            .Add(x => x.Remaining, remaining).Add(x => x.Total, 1000)
            .Add(x => x.ExpiresAt, Now.AddDays(days)).Add(x => x.Now, Now));

        cut.Find("[data-testid=license-gauge]").GetAttribute("data-level").ShouldBe(level);
        cut.Find("[data-testid=gauge-help]").TextContent.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Expired_license_says_so_in_plain_words()
    {
        var cut = Render<LicenseGauge>(p => p
            .Add(x => x.Remaining, 500).Add(x => x.Total, 1000).Add(x => x.ExpiresAt, Now.AddDays(-1)).Add(x => x.Now, Now));

        cut.Find("[data-testid=gauge-summary]").TextContent.ShouldContain("expired");
        cut.Find("[data-testid=gauge-help]").TextContent.ShouldContain("renew");
    }

    [Fact]
    public void Uses_injected_clock_when_now_is_not_supplied()
    {
        Clock.SetUtcNow(Now);
        var cut = Render<LicenseGauge>(p => p.Add(x => x.Remaining, 900).Add(x => x.Total, 1000).Add(x => x.ExpiresAt, Now.AddDays(5)));

        cut.Find("[data-testid=license-gauge]").GetAttribute("data-level").ShouldBe("Critical");
    }
}
