using NexaVerify.Application.Licensing;
using NexaVerify.Domain.Api;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.UnitTests;

public class LicenseAlertRulesTests
{
    private static readonly DateTime Now = new(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly LicenseAlertOptions Options = new();

    private static LicenseAlertCandidate Candidate(
        int total = 1000, int consumed = 0, double endsInDays = 60, LicenseStatus status = LicenseStatus.Active, double startedDaysAgo = 10) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Main pack", status, total, consumed, Now.AddDays(-startedDaysAgo), Now.AddDays(endsInDays));

    private static IReadOnlyList<DueAlert> Due(LicenseAlertCandidate c) => LicenseAlertRules.Evaluate(c, Now, Options);

    [Fact]
    public void A_healthy_license_raises_nothing() => Due(Candidate(consumed: 500)).ShouldBeEmpty();

    [Theory]
    [InlineData(899, false)] // 101 left = 10.1 %
    [InlineData(900, true)] // exactly 10 %
    [InlineData(950, true)]
    public void Low_balance_starts_at_ten_percent_remaining(int consumed, bool due)
    {
        var alerts = Due(Candidate(consumed: consumed));

        alerts.Any(a => a.Type == LicenseAlertType.LowBalance).ShouldBe(due);
        if (due)
        {
            var alert = alerts.Single(a => a.Type == LicenseAlertType.LowBalance);
            alert.EventType.ShouldBe(WebhookEvents.LicenseLowBalance);
            alert.Severity.ShouldBe(AlertSeverity.Warning);
        }
    }

    [Fact]
    public void An_empty_license_is_exhausted_not_just_low()
    {
        var alerts = Due(Candidate(consumed: 1000));

        var alert = alerts.ShouldHaveSingleItem();
        alert.Type.ShouldBe(LicenseAlertType.Exhausted);
        alert.EventType.ShouldBe(WebhookEvents.LicenseExhausted);
        alert.Severity.ShouldBe(AlertSeverity.Critical);
    }

    [Fact]
    public void Topping_up_changes_the_bucket_so_a_new_crossing_can_alert_again()
    {
        var before = Due(Candidate(total: 1000, consumed: 950)).Single().Bucket;
        var afterTopUp = Due(Candidate(total: 2000, consumed: 1950)).Single().Bucket;

        afterTopUp.ShouldNotBe(before);
    }

    [Fact]
    public void The_same_state_always_yields_the_same_bucket_which_is_what_makes_the_job_idempotent()
    {
        var c = Candidate(consumed: 950);

        Due(c).Single().Bucket.ShouldBe(Due(c).Single().Bucket);
    }

    [Theory]
    [InlineData(8, 0)]
    [InlineData(7, 1)]
    [InlineData(3, 1)]
    [InlineData(1.5, 1)]
    [InlineData(1, 1)]
    [InlineData(0.5, 1)]
    public void Expiry_notices_fire_at_seven_days_and_again_at_one_day(double endsInDays, int expected)
    {
        var alerts = Due(Candidate(endsInDays: endsInDays)).Where(a => a.Type == LicenseAlertType.Expiring).ToList();

        alerts.Count.ShouldBe(expected);
    }

    [Fact]
    public void Only_the_tightest_expiry_notice_applies_and_each_has_its_own_bucket()
    {
        var week = Due(Candidate(endsInDays: 5)).Single();
        var day = Due(Candidate(endsInDays: 0.5)).Single();

        week.Bucket.ShouldStartWith("exp7d@");
        day.Bucket.ShouldStartWith("exp1d@");
        week.EventType.ShouldBe(WebhookEvents.LicenseExpiring);
        day.Severity.ShouldBe(AlertSeverity.Critical);
    }

    [Fact]
    public void Renewing_moves_the_end_date_and_so_the_bucket()
    {
        var before = Due(Candidate(endsInDays: 5)).Single().Bucket;
        var afterRenewalAndAnotherApproach = Due(Candidate(endsInDays: 4)).Single().Bucket; // a different end date = a different crossing

        before.ShouldNotBe(afterRenewalAndAnotherApproach);
    }

    [Fact]
    public void A_license_that_just_ended_raises_expired_whether_or_not_the_sweeper_has_run()
    {
        foreach (var status in new[] { LicenseStatus.Active, LicenseStatus.Expired })
        {
            var alert = Due(Candidate(endsInDays: -0.5, status: status)).Single();
            alert.Type.ShouldBe(LicenseAlertType.Expired);
            alert.EventType.ShouldBe(WebhookEvents.LicenseExpired);
        }
    }

    [Fact]
    public void A_license_that_ended_long_ago_is_not_announced()
    {
        Due(Candidate(endsInDays: -(Options.ExpiredLookbackDays + 1), status: LicenseStatus.Expired)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(LicenseStatus.Suspended)]
    [InlineData(LicenseStatus.Inactive)]
    [InlineData(LicenseStatus.Draft)]
    [InlineData(LicenseStatus.Revoked)]
    public void Licenses_that_are_not_in_service_raise_nothing(LicenseStatus status) =>
        Due(Candidate(consumed: 990, endsInDays: 2, status: status)).ShouldBeEmpty();

    [Fact]
    public void A_license_that_has_not_started_raises_no_balance_alert() =>
        Due(Candidate(consumed: 1000, startedDaysAgo: -3)).ShouldBeEmpty();

    [Fact]
    public void Payloads_carry_no_personal_data_and_no_license_key()
    {
        var alert = Due(Candidate(consumed: 950)).Single();

        var json = System.Text.Json.JsonSerializer.Serialize(alert.Payload);

        json.ShouldContain("\"remainingCredits\":50");
        json.ShouldNotContain("licenseKey", Case.Insensitive);
        json.ShouldNotContain("NXV-");
    }

    [Fact]
    public void An_api_key_is_announced_in_the_week_before_it_expires_and_only_then()
    {
        ApiKeyAlertCandidate Key(double endsInDays) => new(Guid.NewGuid(), Guid.NewGuid(), "ci", "nxv_live_abcd", Now.AddDays(endsInDays));

        LicenseAlertRules.Evaluate(Key(10), Now, Options).ShouldBeEmpty();
        LicenseAlertRules.Evaluate(Key(-1), Now, Options).ShouldBeEmpty();
        var alert = LicenseAlertRules.Evaluate(Key(6), Now, Options).Single();
        alert.Type.ShouldBe(LicenseAlertType.ApiKeyExpiring);
        alert.EventType.ShouldBe(WebhookEvents.ApiKeyExpiring);
    }
}
