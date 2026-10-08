using NexaVerify.Domain.Api;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Identity;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Domain.UnitTests;

public class HardeningTests
{
    private static readonly DateTime T0 = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static User NewUser() => User.Create("ada@example.test", "Ada", "hash", Guid.NewGuid(), isPlatformUser: false, mustChangePassword: false);

    // ---- MFA ----

    [Fact]
    public void Enabling_and_disabling_two_factor_ends_every_earlier_session()
    {
        var user = NewUser();
        var version = user.SecurityVersion;

        user.EnableTwoFactor();
        user.TwoFactorEnabled.ShouldBeTrue();
        user.SecurityVersion.ShouldBe(version + 1);

        user.DisableTwoFactor();
        user.TwoFactorEnabled.ShouldBeFalse();
        user.SecurityVersion.ShouldBe(version + 2);
    }

    [Fact]
    public void A_pending_enrolment_is_spent_after_five_wrong_codes_and_can_be_restarted_until_confirmed()
    {
        var user = NewUser();
        var mfa = UserMfa.StartEnrolment(user, [1, 2, 3], T0);

        for (var i = 0; i < UserMfa.MaxConfirmFailures - 1; i++)
        {
            mfa.RegisterConfirmFailure().ShouldBeFalse();
        }

        mfa.RegisterConfirmFailure().ShouldBeTrue();

        mfa.Restart([9], T0.AddMinutes(1));
        mfa.ConfirmFailures.ShouldBe(0);
        mfa.IsConfirmed.ShouldBeFalse();
        mfa.SecretEnc.ShouldBe([9]);

        mfa.Confirm(1234, T0.AddMinutes(2));
        mfa.IsConfirmed.ShouldBeTrue();
        mfa.LastUsedStep.ShouldBe(1234);
        Should.Throw<DomainException>(() => mfa.Restart([7], T0)).Code.ShouldBe("MFA_ALREADY_ENABLED");
    }

    [Fact]
    public void A_challenge_is_live_only_while_unused_unexpired_and_within_its_attempts()
    {
        var challenge = MfaChallenge.Issue(NewUser(), [1], T0, TimeSpan.FromMinutes(5));

        challenge.IsLive(T0.AddMinutes(4), 5).ShouldBeTrue();
        challenge.IsLive(T0.AddMinutes(5), 5).ShouldBeFalse(); // exactly at expiry is dead
        challenge.IsLive(T0.AddMinutes(6), 5).ShouldBeFalse();
        challenge.IsLive(T0, 0).ShouldBeFalse(); // no attempts allowed
        challenge.ExpiresAt.ShouldBe(T0.AddMinutes(5));
    }

    [Fact]
    public void Recovery_codes_and_challenges_belong_to_the_users_client()
    {
        var user = NewUser();

        MfaRecoveryCode.Issue(user, [1], T0).ClientId.ShouldBe(user.ClientId);
        MfaChallenge.Issue(user, [1], T0, TimeSpan.FromMinutes(5)).ClientId.ShouldBe(user.ClientId);
        UserMfa.StartEnrolment(user, [1], T0).ClientId.ShouldBe(user.ClientId);
    }

    // ---- adjustment approvals ----

    private static License NewLicense() => License.Create(Guid.NewGuid(), "L", null, "NXV-TEST", 1000, T0.AddDays(-1), T0.AddDays(30));

    [Fact]
    public void An_adjustment_request_is_pending_until_decided_and_expires_after_its_window()
    {
        var request = LicenseAdjustmentRequest.Create(NewLicense(), 50_000, "goodwill", Guid.NewGuid(), T0, TimeSpan.FromHours(24));

        request.Status.ShouldBe(AdjustmentStatus.Pending);
        request.ExpiresAt.ShouldBe(T0.AddHours(24));
        request.IsOpen(T0.AddHours(23)).ShouldBeTrue();
        request.IsOpen(T0.AddHours(24)).ShouldBeFalse();
        request.EffectiveStatus(T0.AddHours(23)).ShouldBe(AdjustmentStatus.Pending);
        request.EffectiveStatus(T0.AddHours(25)).ShouldBe(AdjustmentStatus.Expired);
    }

    [Fact]
    public void An_adjustment_request_carries_the_licenses_client_and_refuses_zero()
    {
        var license = NewLicense();

        var request = LicenseAdjustmentRequest.Create(license, -20_000, new string('x', 900), Guid.NewGuid(), T0, TimeSpan.FromHours(1));

        (request.ClientId, request.LicenseId, request.Credits).ShouldBe((license.ClientId, license.Id, -20_000));
        request.Reason.Length.ShouldBe(500);
        Should.Throw<DomainException>(() => LicenseAdjustmentRequest.Create(license, 0, "nothing", Guid.NewGuid(), T0, TimeSpan.FromHours(1)));
    }

    // ---- ledger anchors ----

    private static LedgerCheckpoint Checkpoint(Guid license, Guid client, long last = 7, long count = 7, byte fill = 3, int balance = 40) =>
        LedgerCheckpoint.Create(license, client, last, count, Enumerable.Repeat(fill, 32).ToArray(), balance, "m1", T0, bytes => System.Security.Cryptography.SHA256.HashData(bytes));

    [Fact]
    public void Every_field_of_a_checkpoint_is_covered_by_its_signature()
    {
        var license = Guid.NewGuid();
        var client = Guid.NewGuid();
        var baseline = Checkpoint(license, client).Mac;

        Checkpoint(license, client).Mac.ShouldBe(baseline); // deterministic
        Checkpoint(Guid.NewGuid(), client).Mac.ShouldNotBe(baseline);
        Checkpoint(license, Guid.NewGuid()).Mac.ShouldNotBe(baseline);
        Checkpoint(license, client, last: 8).Mac.ShouldNotBe(baseline);
        Checkpoint(license, client, count: 6).Mac.ShouldNotBe(baseline);
        Checkpoint(license, client, fill: 4).Mac.ShouldNotBe(baseline);
        Checkpoint(license, client, balance: 41).Mac.ShouldNotBe(baseline);
    }

    [Fact]
    public void A_ledger_break_record_counts_sightings_and_can_be_cleared()
    {
        var record = LedgerBreakRecord.Open(Guid.NewGuid(), Guid.NewGuid(), "row:5", new string('r', 900), T0);

        record.TimesSeen.ShouldBe(1);
        record.Reason.Length.ShouldBe(400);
        record.Seen(T0.AddDays(1));
        record.Seen(T0.AddDays(2));
        (record.TimesSeen, record.LastSeenAt, record.FirstSeenAt).ShouldBe((3, T0.AddDays(2), T0));
        record.ClearedAt.ShouldBeNull();
        record.Clear(T0.AddDays(3));
        record.ClearedAt.ShouldBe(T0.AddDays(3));
    }

    [Fact]
    public void A_verification_run_moves_from_running_to_completed_or_failed()
    {
        var run = LedgerVerificationRun.Start("manual", Guid.NewGuid(), null, T0);
        run.Status.ShouldBe(LedgerRunStatus.Running);

        run.Complete(5, 100, 1, "[]", T0.AddMinutes(1));
        (run.Status, run.LicensesChecked, run.EntriesChecked, run.BrokenLicenses, run.FinishedAt).ShouldBe((LedgerRunStatus.Completed, 5, 100L, 1, T0.AddMinutes(1)));

        var failed = LedgerVerificationRun.Start("nightly", null, null, T0);
        failed.Fail(new string('e', 900), T0.AddMinutes(2));
        failed.Status.ShouldBe(LedgerRunStatus.Failed);
        failed.Error!.Length.ShouldBe(400);
    }

    // ---- webhooks ----

    [Fact]
    public void Deferring_a_delivery_moves_the_next_attempt_without_counting_one()
    {
        var delivery = WebhookDelivery.Queue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "recognition.completed", "{}", T0);

        delivery.Defer(T0.AddMinutes(5));

        (delivery.Attempts, delivery.Status, delivery.NextAttemptAt).ShouldBe((0, DeliveryStatus.Pending, T0.AddMinutes(5)));
    }
}
