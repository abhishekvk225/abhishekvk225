using NexaVerify.Domain.Common;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Domain.UnitTests;

public class LicenseTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static License NewLicense(int credits = 10) =>
        License.Create(Guid.NewGuid(), "Test", null, "NXV-TEST", credits, Now, Now.AddDays(30));

    [Fact]
    public void A_new_license_is_active_with_all_credits_remaining()
    {
        var l = NewLicense();
        l.Status.ShouldBe(LicenseStatus.Active);
        l.Remaining.ShouldBe(10);
        l.IsUsable(Now.AddDays(1)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(-1)]
    public void Negative_credits_are_rejected(int credits) =>
        Should.Throw<DomainException>(() => License.Create(Guid.NewGuid(), "x", null, "k", credits, Now, Now.AddDays(1)));

    [Fact]
    public void The_period_must_be_positive() =>
        Should.Throw<DomainException>(() => License.Create(Guid.NewGuid(), "x", null, "k", 1, Now, Now));

    [Fact]
    public void Usability_follows_the_period_and_status()
    {
        var l = NewLicense();
        l.IsUsable(Now.AddDays(-1)).ShouldBeFalse(); // not started
        l.IsUsable(Now.AddDays(30)).ShouldBeFalse(); // end is exclusive
        l.IsUsable(Now.AddDays(1), cost: 11).ShouldBeFalse();
        l.Suspend("r", Now);
        l.IsUsable(Now.AddDays(1)).ShouldBeFalse();
    }

    [Fact]
    public void Past_the_end_date_the_effective_status_is_expired_even_before_the_sweeper_runs()
    {
        var l = NewLicense();
        l.EffectiveStatus(Now.AddDays(31)).ShouldBe(LicenseStatus.Expired);
        l.Status.ShouldBe(LicenseStatus.Active);
    }

    [Fact]
    public void Suspend_requires_a_reason_and_an_active_license()
    {
        var l = NewLicense();
        Should.Throw<DomainException>(() => l.Suspend(" ", Now));
        l.Suspend("overdue", Now);
        l.SuspendedReason.ShouldBe("overdue");
        Should.Throw<DomainException>(() => l.Suspend("again", Now));
        l.Activate(Now);
        l.SuspendedReason.ShouldBeNull();
    }

    [Fact]
    public void An_expired_license_cannot_be_activated_but_can_be_renewed()
    {
        var l = NewLicense();
        l.Expire(Now.AddDays(31)).ShouldBe(10);
        l.Remaining.ShouldBe(0);
        Should.Throw<DomainException>(() => l.Activate(Now.AddDays(31)));
        l.Renew(Now.AddDays(90), 5, Now.AddDays(31));
        l.Status.ShouldBe(LicenseStatus.Active);
        l.Remaining.ShouldBe(5);
    }

    [Fact]
    public void Expire_does_nothing_before_the_end_date_or_twice()
    {
        var l = NewLicense();
        l.Expire(Now.AddDays(1)).ShouldBeNull();
        l.Expire(Now.AddDays(31)).ShouldNotBeNull();
        l.Expire(Now.AddDays(32)).ShouldBeNull();
    }

    [Fact]
    public void Revoke_is_terminal_and_writes_off_unused_credits()
    {
        var l = NewLicense();
        l.Revoke("contract ended").ShouldBe(10);
        l.Remaining.ShouldBe(0);
        Should.Throw<DomainException>(() => l.Revoke("again"));
        Should.Throw<DomainException>(() => l.Renew(Now.AddDays(60), 1, Now));
        Should.Throw<DomainException>(() => l.Activate(Now));
    }

    [Fact]
    public void Adjustments_cannot_make_the_balance_negative_or_be_zero()
    {
        var l = NewLicense();
        l.AdjustCredits(5);
        l.Remaining.ShouldBe(15);
        Should.Throw<DomainException>(() => l.AdjustCredits(0));
        Should.Throw<DomainException>(() => l.AdjustCredits(-16));
        l.AdjustCredits(-15);
        l.Remaining.ShouldBe(0);
    }

    [Fact]
    public void Renewal_must_move_the_end_date_forward()
    {
        var l = NewLicense();
        Should.Throw<DomainException>(() => l.Renew(Now.AddDays(10), 0, Now));
        Should.Throw<DomainException>(() => l.Renew(Now.AddDays(40), -1, Now));
        l.Renew(Now.AddDays(40), 0, Now);
        l.ExpiresAt.ShouldBe(Now.AddDays(40));
    }

    [Fact]
    public void Ledger_hash_covers_content_and_chain()
    {
        var l = NewLicense();
        var first = LicenseTransaction.Create(l, LedgerEntryType.Grant, 10, 0, null, null, null, null, "g", "System", null, null, null, Now);
        var second = LicenseTransaction.Create(l, LedgerEntryType.Consume, -1, 10, MeteredOperation.Verify, null, null, "k", null, "User", Guid.NewGuid(), "c", first.RowHash, Now.AddSeconds(1));

        first.PrevHash.ShouldBe(LicenseTransaction.Genesis);
        second.PrevHash.ShouldBe(first.RowHash);
        second.ComputeHash().ShouldBe(second.RowHash);
        second.BalanceAfter.ShouldBe(9);

        var other = LicenseTransaction.Create(l, LedgerEntryType.Consume, -1, 10, MeteredOperation.Verify, null, null, "k", null, "User", second.ActorId, "c", LicenseTransaction.Genesis, Now.AddSeconds(1));
        other.RowHash.ShouldNotBe(second.RowHash); // a different predecessor yields a different hash
    }

    [Theory]
    [InlineData(ChargePolicy.OnCompleted, MeterOutcome.Success, true)]
    [InlineData(ChargePolicy.OnCompleted, MeterOutcome.NoMatch, true)]
    [InlineData(ChargePolicy.OnCompleted, MeterOutcome.Failed, false)]
    [InlineData(ChargePolicy.OnSuccess, MeterOutcome.Success, true)]
    [InlineData(ChargePolicy.OnSuccess, MeterOutcome.NoMatch, false)]
    [InlineData(ChargePolicy.OnSuccess, MeterOutcome.Failed, false)]
    [InlineData(ChargePolicy.OnAttempt, MeterOutcome.Failed, true)]
    public void Charge_policy_rules(ChargePolicy policy, MeterOutcome outcome, bool charged) =>
        policy.ShouldCharge(outcome).ShouldBe(charged);
}

public class LicenseEffectiveStatusTests
{
    private static readonly DateTime Now = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(LicenseStatus.Active, 1, LicenseStatus.Expired)]
    [InlineData(LicenseStatus.Suspended, 1, LicenseStatus.Expired)]
    [InlineData(LicenseStatus.Inactive, 0, LicenseStatus.Expired)]
    [InlineData(LicenseStatus.Draft, 1, LicenseStatus.Expired)]
    [InlineData(LicenseStatus.Active, -1, LicenseStatus.Active)]
    [InlineData(LicenseStatus.Revoked, 5, LicenseStatus.Revoked)]
    [InlineData(LicenseStatus.Expired, -5, LicenseStatus.Expired)]
    public void The_static_rule_and_the_instance_rule_agree(LicenseStatus stored, int daysPastEnd, LicenseStatus expected)
    {
        var end = Now.AddDays(-daysPastEnd);

        License.EffectiveStatusOf(stored, end, Now).ShouldBe(expected);
    }
}
