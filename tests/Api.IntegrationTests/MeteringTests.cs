using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Application.Common;
using NexaVerify.Application.Licensing;
using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Domain.Licensing;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>The money path: atomic deduction, blocking rules, idempotency, FEFO, refunds and the immutable ledger — against real SQL Server.</summary>
[Collection(SqlServerCollection.Name)]
public class MeteringTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private AuthApp _app = null!;
    private LoginResponse _platform = null!;

    public MeteringTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _app = await AuthApp.CreateAsync(_fixture);
        _platform = await _app.SuperAdminAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task<Guid> NewClientAsync(string code) => (await _app.OnboardClientAsync(_platform.AccessToken, code, $"a@{code.ToLowerInvariant()}.test")).Client.Id;

    /// <summary>Inserts a license directly (so tests can create past periods, which the API rightly refuses) with its Grant ledger row.</summary>
    private Task<Guid> SeedLicenseAsync(Guid clientId, int credits, DateTime startsAt, DateTime expiresAt, LicenseStatus status = LicenseStatus.Active) =>
        _app.WithServicesAsync(null, async sp =>
        {
            var db = sp.GetRequiredService<NexaVerify.Infrastructure.Persistence.AppDbContext>();
            var license = License.Create(clientId, "Seeded " + Guid.NewGuid().ToString("N")[..6], null, LicenseKeyGenerator.Generate(), credits, startsAt, expiresAt);
            db.Licenses.Add(license);
            await db.SaveChangesAsync();
            await sp.GetRequiredService<LedgerWriter>().AppendAsync(license, LedgerEntryType.Grant, credits, 0, default, reason: "seed");
            await db.SaveChangesAsync();
            if (status != LicenseStatus.Active)
            {
                if (status == LicenseStatus.Suspended)
                {
                    license.Suspend("test", DateTime.UtcNow);
                }
                else if (status == LicenseStatus.Inactive)
                {
                    license.Deactivate();
                }

                await db.SaveChangesAsync();
            }

            return license.Id;
        });

    private Task<Result<ChargeResult>> ChargeAsync(Guid clientId, MeteredOperation op = MeteredOperation.Verify, MeterOutcome outcome = MeterOutcome.Success, string? key = null) =>
        _app.WithServicesAsync(clientId, sp => sp.GetRequiredService<ILicenseMeteringService>().ChargeAsync(new ChargeCommand(clientId, op, outcome, null, key), default));

    private Task<Result<MeterTicket>> PreflightAsync(Guid clientId, MeteredOperation op = MeteredOperation.Verify) =>
        _app.WithServicesAsync(clientId, sp => sp.GetRequiredService<ILicenseMeteringService>().PreflightAsync(clientId, op, default));

    private Task<(int Total, int Consumed)> BalanceAsync(Guid licenseId) =>
        _app.WithDbAsync(async db =>
        {
            var l = await db.Licenses.AsNoTracking().SingleAsync(x => x.Id == licenseId);
            return (l.TotalCredits, l.ConsumedCredits);
        });

    private Task<LedgerVerificationDto> VerifyAsync(Guid licenseId) =>
        _app.WithServicesAsync(null, async sp => (await sp.GetRequiredService<ILicenseService>().VerifyLedgerAsync(licenseId, default)).Value!);

    [Fact]
    public async Task A_billable_operation_deducts_its_cost_and_writes_a_ledger_row()
    {
        var client = await NewClientAsync("M1");
        var license = await SeedLicenseAsync(client, 10, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));

        var identify = await ChargeAsync(client, MeteredOperation.Identify);
        identify.IsSuccess.ShouldBeTrue();
        identify.Value!.Charged.ShouldBe(2); // seeded platform default
        identify.Value.RemainingBalance.ShouldBe(8);
        identify.Value.LicenseId.ShouldBe(license);

        (await BalanceAsync(license)).ShouldBe((10, 2));

        var detect = await ChargeAsync(client, MeteredOperation.Detect);
        detect.Value!.Charged.ShouldBe(0); // free
        (await BalanceAsync(license)).Consumed.ShouldBe(2);

        var ledger = await _app.WithDbAsync(db => db.LicenseTransactions.AsNoTracking().Where(t => t.LicenseId == license).OrderBy(t => t.Id).ToListAsync());
        ledger.Select(t => (t.Type, t.Credits, t.BalanceAfter)).ShouldBe([(LedgerEntryType.Grant, 10, 10), (LedgerEntryType.Consume, -2, 8)]);
        ledger[1].Operation.ShouldBe(MeteredOperation.Identify);
        (await VerifyAsync(license)).Valid.ShouldBeTrue();
    }

    [Fact]
    public async Task Charging_depends_on_the_outcome_and_the_charge_policy()
    {
        var client = await NewClientAsync("M2");
        var license = await SeedLicenseAsync(client, 10, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));

        // Enroll = OnSuccess: a failed enrolment is free, a successful one costs 1
        (await ChargeAsync(client, MeteredOperation.Enroll, MeterOutcome.Failed)).Value!.Charged.ShouldBe(0);
        (await ChargeAsync(client, MeteredOperation.Enroll, MeterOutcome.NoMatch)).Value!.Charged.ShouldBe(0);
        (await ChargeAsync(client, MeteredOperation.Enroll, MeterOutcome.Success)).Value!.Charged.ShouldBe(1);

        // Verify = OnCompleted: "no match" is a definitive answer and billable, a provider failure is not
        (await ChargeAsync(client, MeteredOperation.Verify, MeterOutcome.NoMatch)).Value!.Charged.ShouldBe(1);
        (await ChargeAsync(client, MeteredOperation.Verify, MeterOutcome.Failed)).Value!.Charged.ShouldBe(0);

        (await BalanceAsync(license)).Consumed.ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_charges_can_never_overdraw_a_license()
    {
        var client = await NewClientAsync("M3");
        var license = await SeedLicenseAsync(client, 25, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));

        var results = await Task.WhenAll(Enumerable.Range(0, 80).Select(_ => Task.Run(() => ChargeAsync(client))));

        var succeeded = results.Count(r => r.IsSuccess && r.Value!.Charged == 1);
        var rejected = results.Where(r => r.IsFailure).ToList();
        succeeded.ShouldBe(25);
        rejected.Count.ShouldBe(55);
        rejected.ShouldAllBe(r => r.Error!.Code == ErrorCodes.LicenseInsufficientBalance);

        (await BalanceAsync(license)).ShouldBe((25, 25));
        var ledger = await _app.WithDbAsync(db => db.LicenseTransactions.AsNoTracking().Where(t => t.LicenseId == license && t.Type == LedgerEntryType.Consume).CountAsync());
        ledger.ShouldBe(25);

        // 80 racing writers still produced one unbroken hash chain
        (await VerifyAsync(license)).Valid.ShouldBeTrue();
    }

    [Fact]
    public async Task The_same_idempotency_key_never_charges_twice_even_when_replayed_concurrently()
    {
        var client = await NewClientAsync("M4");
        var license = await SeedLicenseAsync(client, 50, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => ChargeAsync(client, key: "req-123"))));

        results.ShouldAllBe(r => r.IsSuccess && r.Value!.Charged == 1);
        results.Count(r => r.Value!.Replayed).ShouldBeGreaterThanOrEqualTo(0);
        (await BalanceAsync(license)).Consumed.ShouldBe(1);

        var replay = await ChargeAsync(client, key: "req-123");
        replay.Value!.Replayed.ShouldBeTrue();
        replay.Value.Charged.ShouldBe(1);
        (await BalanceAsync(license)).Consumed.ShouldBe(1);

        (await ChargeAsync(client, key: "req-456")).Value!.Replayed.ShouldBeFalse();
        (await BalanceAsync(license)).Consumed.ShouldBe(2);
    }

    [Fact]
    public async Task Idempotency_keys_are_scoped_per_client()
    {
        var a = await NewClientAsync("M5A");
        var b = await NewClientAsync("M5B");
        var la = await SeedLicenseAsync(a, 5, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));
        var lb = await SeedLicenseAsync(b, 5, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));

        (await ChargeAsync(a, key: "same")).IsSuccess.ShouldBeTrue();
        var second = await ChargeAsync(b, key: "same");

        second.Value!.Replayed.ShouldBeFalse();
        (await BalanceAsync(la)).Consumed.ShouldBe(1);
        (await BalanceAsync(lb)).Consumed.ShouldBe(1);
    }

    [Fact]
    public async Task Licenses_are_consumed_earliest_expiry_first_and_never_split()
    {
        var client = await NewClientAsync("M6");
        var later = await SeedLicenseAsync(client, 5, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(90));
        var sooner = await SeedLicenseAsync(client, 1, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(10));

        (await ChargeAsync(client)).Value!.LicenseId.ShouldBe(sooner);
        (await ChargeAsync(client)).Value!.LicenseId.ShouldBe(later); // sooner is now empty
        // identify costs 2 and 'later' has 4 left → fine; the charge is never split across licenses
        (await ChargeAsync(client, MeteredOperation.Identify)).Value!.LicenseId.ShouldBe(later);
        (await BalanceAsync(later)).Consumed.ShouldBe(3);
        (await BalanceAsync(sooner)).Consumed.ShouldBe(1);
    }

    [Fact]
    public async Task Operations_are_blocked_with_a_specific_reason_when_no_license_can_be_used()
    {
        // none at all
        var none = await NewClientAsync("M7N");
        var r = await ChargeAsync(none);
        r.Error!.Code.ShouldBe(ErrorCodes.LicenseNotFound);
        (await PreflightAsync(none)).Error!.Code.ShouldBe(ErrorCodes.LicenseNotFound);

        // expired (period over, sweeper has not run)
        var expired = await NewClientAsync("M7E");
        var expiredLicense = await SeedLicenseAsync(expired, 10, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddDays(-1));
        (await ChargeAsync(expired)).Error!.Code.ShouldBe(ErrorCodes.LicenseExpired);
        (await PreflightAsync(expired)).Error!.Code.ShouldBe(ErrorCodes.LicenseExpired);
        (await BalanceAsync(expiredLicense)).Consumed.ShouldBe(0);

        // not started yet counts as no usable license
        var future = await NewClientAsync("M7F");
        await SeedLicenseAsync(future, 10, DateTime.UtcNow.AddDays(5), DateTime.UtcNow.AddDays(35));
        (await ChargeAsync(future)).IsFailure.ShouldBeTrue();

        // suspended
        var suspended = await NewClientAsync("M7S");
        var suspendedLicense = await SeedLicenseAsync(suspended, 10, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30), LicenseStatus.Suspended);
        (await ChargeAsync(suspended)).Error!.Code.ShouldBe(ErrorCodes.LicenseSuspended);
        (await BalanceAsync(suspendedLicense)).Consumed.ShouldBe(0);

        // deactivated
        var inactive = await NewClientAsync("M7I");
        await SeedLicenseAsync(inactive, 10, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30), LicenseStatus.Inactive);
        (await ChargeAsync(inactive)).IsFailure.ShouldBeTrue();

        // used up
        var depleted = await NewClientAsync("M7D");
        await SeedLicenseAsync(depleted, 1, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));
        (await ChargeAsync(depleted)).IsSuccess.ShouldBeTrue();
        (await ChargeAsync(depleted)).Error!.Code.ShouldBe(ErrorCodes.LicenseInsufficientBalance);
        (await PreflightAsync(depleted)).Error!.Code.ShouldBe(ErrorCodes.LicenseInsufficientBalance);

        // not enough for a costlier operation, though credits remain
        var short_ = await NewClientAsync("M7X");
        await SeedLicenseAsync(short_, 1, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));
        (await PreflightAsync(short_, MeteredOperation.Identify)).Error!.Code.ShouldBe(ErrorCodes.LicenseInsufficientBalance);
        (await PreflightAsync(short_, MeteredOperation.Detect)).IsSuccess.ShouldBeTrue(); // free, but still needs a usable license
    }

    [Fact]
    public async Task A_suspended_client_with_a_good_license_status_is_not_charged_across_tenants()
    {
        var a = await NewClientAsync("M8A");
        var b = await NewClientAsync("M8B");
        var la = await SeedLicenseAsync(a, 10, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));

        // B cannot consume A's credits: its charge finds no license of its own
        (await ChargeAsync(b)).Error!.Code.ShouldBe(ErrorCodes.LicenseNotFound);
        (await BalanceAsync(la)).Consumed.ShouldBe(0);

        // and even a forged command naming A while running as tenant B is invisible (query filter + RLS)
        var forged = await _app.WithServicesAsync(b, sp => sp.GetRequiredService<ILicenseMeteringService>()
            .ChargeAsync(new ChargeCommand(a, MeteredOperation.Verify, MeterOutcome.Success, null, null), default));
        forged.IsFailure.ShouldBeTrue();
        (await BalanceAsync(la)).Consumed.ShouldBe(0);
    }

    [Fact]
    public async Task Customer_consumption_can_be_refunded_once()
    {
        var client = await NewClientAsync("M9");
        var license = await SeedLicenseAsync(client, 10, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));
        var charge = (await ChargeAsync(client, MeteredOperation.Identify)).Value!;

        var refund = await _app.PostAsync($"/api/v1/admin/transactions/{charge.TransactionId}/refund", new RefundRequest("Provider outage"), _platform.AccessToken);
        refund.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entry = (await refund.Content.ReadFromJsonAsync<LicenseTransactionDto>(AuthApp.Json))!;
        entry.Type.ShouldBe("Refund");
        entry.Credits.ShouldBe(2);
        entry.ReferenceTransactionId.ShouldBe(charge.TransactionId);
        (await BalanceAsync(license)).Consumed.ShouldBe(0);

        (await _app.PostAsync($"/api/v1/admin/transactions/{charge.TransactionId}/refund", new RefundRequest("again"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _app.PostAsync("/api/v1/admin/transactions/999999/refund", new RefundRequest("x"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await BalanceAsync(license)).Consumed.ShouldBe(0);
        (await VerifyAsync(license)).Valid.ShouldBeTrue();
    }

    [Fact]
    public async Task Only_a_consumption_can_be_refunded()
    {
        var client = await NewClientAsync("M10");
        var license = await SeedLicenseAsync(client, 10, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));
        var grantId = await _app.WithDbAsync(db => db.LicenseTransactions.Where(t => t.LicenseId == license).Select(t => t.Id).SingleAsync());

        (await _app.PostAsync($"/api/v1/admin/transactions/{grantId}/refund", new RefundRequest("x"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task The_ledger_is_immutable_and_tampering_is_detected()
    {
        var client = await NewClientAsync("M11");
        var license = await SeedLicenseAsync(client, 10, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));
        await ChargeAsync(client);
        await ChargeAsync(client);

        // the append-only trigger refuses UPDATE and DELETE even for a fully privileged connection
        var update = await Should.ThrowAsync<Exception>(() => _app.WithDbAsync(db =>
            db.Database.ExecuteSqlRawAsync("UPDATE licensing.LicenseTransactions SET Credits = 0 WHERE LicenseId = {0}", license)));
        update.ToString().ShouldContain("append-only");
        var delete = await Should.ThrowAsync<Exception>(() => _app.WithDbAsync(db =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM licensing.LicenseTransactions WHERE LicenseId = {0}", license)));
        delete.ToString().ShouldContain("append-only");

        // an attacker who disables the trigger and edits a row is still caught by the hash chain
        await _app.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("DISABLE TRIGGER licensing.trg_LicenseTransactions_AppendOnly ON licensing.LicenseTransactions");
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE licensing.LicenseTransactions SET Reason = 'forged' WHERE LicenseId = {0} AND Type = 'Consume' AND Id = (SELECT MIN(Id) FROM licensing.LicenseTransactions WHERE LicenseId = {0} AND Type = 'Consume')", license);
            await db.Database.ExecuteSqlRawAsync("ENABLE TRIGGER licensing.trg_LicenseTransactions_AppendOnly ON licensing.LicenseTransactions");
            return 0;
        });

        var verification = await VerifyAsync(license);
        verification.Valid.ShouldBeFalse();
        verification.Problems.ShouldContain(p => p.Contains("does not match its hash"));
    }

    [Fact]
    public async Task The_database_refuses_an_overdrawn_license_even_if_the_application_is_bypassed()
    {
        var client = await NewClientAsync("M12");
        var license = await SeedLicenseAsync(client, 5, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));

        var ex = await Should.ThrowAsync<Exception>(() => _app.WithDbAsync(db =>
            db.Database.ExecuteSqlRawAsync("UPDATE licensing.Licenses SET ConsumedCredits = 6 WHERE Id = {0}", license)));
        ex.ToString().ShouldContain("CK_Licenses_Credits");
    }

    [Fact]
    public async Task The_sweeper_expires_lapsed_licenses_and_writes_off_the_unused_credits()
    {
        var client = await NewClientAsync("M13");
        var lapsed = await SeedLicenseAsync(client, 10, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddMinutes(-5));
        var current = await SeedLicenseAsync(client, 10, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));

        var processed = await _app.WithServicesAsync(null, sp => sp.GetRequiredService<ILicenseExpiryProcessor>().ProcessAsync(100, default));
        processed.ShouldBeGreaterThanOrEqualTo(1);

        var (status, total) = await _app.WithDbAsync(async db =>
        {
            var l = await db.Licenses.AsNoTracking().SingleAsync(x => x.Id == lapsed);
            return (l.Status, l.TotalCredits);
        });
        status.ShouldBe(LicenseStatus.Expired);
        total.ShouldBe(0); // unused credits written off (Consumed unchanged = 0)

        var ledger = await _app.WithDbAsync(db => db.LicenseTransactions.AsNoTracking().Where(t => t.LicenseId == lapsed).OrderBy(t => t.Id).Select(t => t.Type).ToListAsync());
        ledger.ShouldBe([LedgerEntryType.Grant, LedgerEntryType.ExpiryWriteOff]);
        (await VerifyAsync(lapsed)).Valid.ShouldBeTrue();
        (await BalanceAsync(current)).ShouldBe((10, 0));

        // running again is a no-op
        (await _app.WithServicesAsync(null, sp => sp.GetRequiredService<ILicenseExpiryProcessor>().ProcessAsync(100, default))).ShouldBe(0);
    }

    [Fact]
    public async Task A_renewed_expired_license_is_usable_again()
    {
        var client = await NewClientAsync("M14");
        var lapsed = await SeedLicenseAsync(client, 10, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddMinutes(-5));
        (await ChargeAsync(client)).Error!.Code.ShouldBe(ErrorCodes.LicenseExpired);
        await _app.WithServicesAsync(null, sp => sp.GetRequiredService<ILicenseExpiryProcessor>().ProcessAsync(100, default));

        var renewed = await _app.PostAsync($"/api/v1/admin/licenses/{lapsed}/renew", new RenewLicenseRequest(DateTime.UtcNow.AddDays(30), 20, "Renewed"), _platform.AccessToken);
        renewed.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ChargeAsync(client)).Value!.Charged.ShouldBe(1);
        (await BalanceAsync(lapsed)).ShouldBe((20, 1));
        (await VerifyAsync(lapsed)).Valid.ShouldBeTrue();
    }

    [Fact]
    public async Task Admin_changes_and_charges_interleave_without_breaking_the_chain()
    {
        var client = await NewClientAsync("M15");
        var license = await SeedLicenseAsync(client, 200, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));

        var charges = Enumerable.Range(0, 40).Select(_ => Task.Run(() => ChargeAsync(client)));
        var adjusts = Enumerable.Range(0, 5).Select(i => Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var r = await _app.PostAsync($"/api/v1/admin/licenses/{license}/adjust", new AdjustLicenseRequest(1, "bump " + i), _platform.AccessToken);
                if (r.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
        }));
        await Task.WhenAll(charges.Concat(adjusts));

        (await VerifyAsync(license)).Valid.ShouldBeTrue();
    }

    [Fact]
    public async Task Cost_overrides_change_what_is_charged()
    {
        var client = await NewClientAsync("M16");
        var license = await SeedLicenseAsync(client, 100, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));
        (await _app.PutAsync($"/api/v1/admin/cost-rules/clients/{client}", new SetCostRuleRequest("Verify", 5, "OnAttempt", null), _platform.AccessToken)).EnsureSuccessStatusCode();

        var charge = await ChargeAsync(client, MeteredOperation.Verify, MeterOutcome.Failed); // OnAttempt charges even failures
        charge.Value!.Charged.ShouldBe(5);
        (await BalanceAsync(license)).Consumed.ShouldBe(5);
        (await PreflightAsync(client)).Value!.Cost.ShouldBe(5);
    }
}
