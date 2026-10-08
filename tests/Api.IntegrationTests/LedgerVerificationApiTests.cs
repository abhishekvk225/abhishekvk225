using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Application.Licensing;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Domain.Licensing;
using NexaVerify.Infrastructure.Background;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>The platform-wide ledger tamper check: non-blocking endpoint, nightly job, keyed checkpoints, de-duplication and real (trigger-bypassing) tampers.</summary>
[Collection(SqlServerCollection.Name)]
public class LedgerVerificationApiTests : UsageTestBase
{
    private const string Url = "/api/v1/admin/licensing/verify-ledger";

    public LedgerVerificationApiTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>Starts a check (expects 202), then polls the run until it is no longer Running.</summary>
    private async Task<LedgerRunDto> VerifyAsync(Guid? licenseId = null)
    {
        var response = await App.PostAsync(Url, licenseId is null ? null : new VerifyLedgerRequest(licenseId), Platform.AccessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var started = (await response.Content.ReadFromJsonAsync<LedgerRunDto>(AuthApp.Json))!;
        started.Status.ShouldBe("Running");
        response.Headers.Location!.ToString().ShouldEndWith($"{Url}/runs/{started.Id}");

        for (var i = 0; i < 300; i++)
        {
            var run = await GetAsync<LedgerRunDto>($"{Url}/runs/{started.Id}", Platform.AccessToken);
            if (run.Status != "Running")
            {
                return run;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The verification run did not finish.");
    }

    /// <summary>Bypasses the append-only trigger the way a DBA with raw access could, then restores it. Used only to prove the check detects it.</summary>
    private Task WithTriggerOffAsync(Func<Microsoft.EntityFrameworkCore.DbContext, Task> tamper) =>
        App.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE licensing.LicenseTransactions DISABLE TRIGGER trg_LicenseTransactions_AppendOnly");
            try
            {
                await tamper(db);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE licensing.LicenseTransactions ENABLE TRIGGER trg_LicenseTransactions_AppendOnly");
            }

            return true;
        });

    private Task TamperWithReasonAsync(long entryId) =>
        WithTriggerOffAsync(async db => (await db.Database.ExecuteSqlAsync($"UPDATE licensing.LicenseTransactions SET Reason = N'forged' WHERE Id = {entryId}")).ShouldBe(1));

    private Task<List<long>> EntryIdsAsync(Tenant t) =>
        App.WithTenantDbAsync(t.ClientId, db => db.LicenseTransactions.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id).ToListAsync());

    [Fact]
    public async Task An_untouched_platform_verifies_clean_and_anchors_every_ledger()
    {
        var a = await NewTenantAsync("V1", credits: 100);
        var b = await NewTenantAsync("V1B", credits: 100);
        await RunRecognitionsAsync(a, seed: 10);
        await RunRecognitionsAsync(b, seed: 70);

        var run = await VerifyAsync();

        run.Status.ShouldBe("Completed");
        run.LicensesChecked.ShouldBe(2);
        run.EntriesChecked.ShouldBe(2 * (1 + 5)); // a grant and five charges each
        run.BrokenLicenses.ShouldBe(0);
        run.Breaks.ShouldBeEmpty();
        (await App.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == "ledger.verification_failed"))).ShouldBe(0);
        (await App.WithDbAsync(db => db.LedgerCheckpoints.CountAsync())).ShouldBe(2);
        (await App.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == "ledger.verification_requested"))).ShouldBe(1);
    }

    [Fact]
    public async Task A_real_tamper_is_found_pinned_to_its_row_and_audited()
    {
        var a = await NewTenantAsync("V2", credits: 100);
        var b = await NewTenantAsync("V2B", credits: 100);
        await RunRecognitionsAsync(a, seed: 10);
        await RunRecognitionsAsync(b, seed: 70);
        var victim = (await EntryIdsAsync(a))[3];
        await TamperWithReasonAsync(victim);

        var run = await VerifyAsync();

        run.BrokenLicenses.ShouldBe(1);
        var broken = run.Breaks.Single();
        (broken.LicenseId, broken.ClientId, broken.FirstBrokenEntryId).ShouldBe((a.LicenseId, a.ClientId, victim));
        broken.Reason.ShouldContain("does not match its hash");
        var audit = await App.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(l => l.Action == "ledger.verification_failed").ToListAsync());
        var entry = audit.ShouldHaveSingleItem();
        entry.ClientId.ShouldBe(a.ClientId);
        entry.EntityId.ShouldBe(a.LicenseId.ToString());

        // the per-license check agrees, and the other client is unaffected
        (await GetAsync<LedgerVerificationDto>($"/api/v1/admin/licenses/{a.LicenseId}/verify-ledger", Platform.AccessToken)).Valid.ShouldBeFalse();
        (await GetAsync<LedgerVerificationDto>($"/api/v1/admin/licenses/{b.LicenseId}/verify-ledger", Platform.AccessToken)).Valid.ShouldBeTrue();
        (await App.WithDbAsync(db => db.LedgerCheckpoints.AsNoTracking().Select(c => c.LicenseId).ToListAsync())).ShouldBe([b.LicenseId]); // the broken ledger is never anchored
    }

    [Fact]
    public async Task A_persistent_break_is_alerted_once_and_listed_for_the_operator_until_it_is_fixed()
    {
        var a = await NewTenantAsync("V9", credits: 100);
        await RunRecognitionsAsync(a, seed: 10);
        var victim = (await EntryIdsAsync(a))[2];
        await TamperWithReasonAsync(victim);

        (await VerifyAsync()).BrokenLicenses.ShouldBe(1);
        (await VerifyAsync()).BrokenLicenses.ShouldBe(1);
        (await VerifyAsync()).BrokenLicenses.ShouldBe(1);

        var open = await GetAsync<List<LedgerOpenBreakDto>>("/api/v1/admin/licensing/ledger-breaks", Platform.AccessToken);
        var item = open.ShouldHaveSingleItem();
        (item.LicenseId, item.ClientId, item.BreakKey).ShouldBe((a.LicenseId, a.ClientId, $"row:{victim}"));
        item.TimesSeen.ShouldBe(3);
        item.AlertedAt.ShouldNotBeNull();
        item.LastReminderAt.ShouldBeNull("three runs in a row do not re-announce it");
        (await App.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == "ledger.verification_failed"))).ShouldBe(1);
    }

    [Fact]
    public async Task The_open_break_list_is_for_platform_operators_only_and_empty_on_a_healthy_platform()
    {
        const string url = "/api/v1/admin/licensing/ledger-breaks";
        var a = await NewTenantAsync("V10", credits: 100);

        (await App.GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await App.GetAsync(url, a.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync<List<LedgerOpenBreakDto>>(url, Platform.AccessToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_tamper_that_recomputes_the_whole_unkeyed_chain_is_still_caught_by_the_signed_checkpoint()
    {
        var a = await NewTenantAsync("V7", credits: 100);
        await RunRecognitionsAsync(a, seed: 10);
        (await VerifyAsync()).BrokenLicenses.ShouldBe(0); // anchors the honest head

        // A careful attacker with database access: rewrites a charge, recomputes RowHash/PrevHash for every later row.
        var rewritten = await App.WithDbAsync(async db =>
        {
            var rows = await db.LicenseTransactions.AsNoTracking().Where(t => t.LicenseId == a.LicenseId).OrderBy(t => t.Id).ToListAsync();
            byte[] previous = rows[1].RowHash;
            var victim = rows[2];
            typeof(LicenseTransaction).GetProperty(nameof(LicenseTransaction.Reason))!.SetValue(victim, "innocent looking");
            for (var i = 2; i < rows.Count; i++)
            {
                typeof(LicenseTransaction).GetProperty(nameof(LicenseTransaction.PrevHash))!.SetValue(rows[i], previous);
                typeof(LicenseTransaction).GetProperty(nameof(LicenseTransaction.RowHash))!.SetValue(rows[i], rows[i].ComputeHash());
                previous = rows[i].RowHash;
            }

            return rows;
        });
        await WithTriggerOffAsync(async db =>
        {
            // (LicenseId, PrevHash) is unique: move the rewritten tail out of the way, row by row, from the end.
            foreach (var row in rewritten.Skip(2).Reverse())
            {
                var affected = await db.Database.ExecuteSqlRawAsync(
                    "UPDATE licensing.LicenseTransactions SET Reason = @reason, PrevHash = @prev, RowHash = @hash WHERE Id = @id",
                    new SqlParameter("@reason", (object?)row.Reason ?? DBNull.Value), new SqlParameter("@prev", row.PrevHash),
                    new SqlParameter("@hash", row.RowHash), new SqlParameter("@id", row.Id));
                affected.ShouldBe(1);
            }
        });

        // The plain, unkeyed check cannot see it...
        (await GetAsync<LedgerVerificationDto>($"/api/v1/admin/licenses/{a.LicenseId}/verify-ledger", Platform.AccessToken)).Valid.ShouldBeFalse();
        var reloaded = await App.WithDbAsync(db => db.LicenseTransactions.AsNoTracking().Where(t => t.LicenseId == a.LicenseId).OrderBy(t => t.Id).ToListAsync());
        var remaining = await App.WithDbAsync(async db => (await db.Licenses.AsNoTracking().SingleAsync(l => l.Id == a.LicenseId)).Remaining);
        LedgerVerifier.Analyse(remaining, reloaded).ShouldBeEmpty();

        // ...but the keyed checkpoint, whose MAC the attacker cannot recompute, flags it, in the nightly run as well.
        var run = await VerifyAsync();
        var broken = run.Breaks.ShouldHaveSingleItem();
        broken.LicenseId.ShouldBe(a.LicenseId);
        broken.Reason.ShouldContain("signed checkpoint");
    }

    [Fact]
    public async Task Deleting_the_newest_rows_and_fixing_the_balance_is_caught_by_the_checkpoint()
    {
        var a = await NewTenantAsync("V8", credits: 100);
        await RunRecognitionsAsync(a, seed: 10);
        (await VerifyAsync()).BrokenLicenses.ShouldBe(0);

        var ids = await EntryIdsAsync(a);
        await WithTriggerOffAsync(async db =>
        {
            await db.Database.ExecuteSqlAsync($"DELETE FROM licensing.LicenseTransactions WHERE LicenseId = {a.LicenseId} AND Id IN ({ids[^1]}, {ids[^2]})");
            // the attacker also puts the license balance back in line with what is left of the ledger
            await db.Database.ExecuteSqlAsync($"UPDATE licensing.Licenses SET ConsumedCredits = ConsumedCredits - 2 WHERE Id = {a.LicenseId}");
        });
        (await GetAsync<LedgerVerificationDto>($"/api/v1/admin/licenses/{a.LicenseId}/verify-ledger", Platform.AccessToken)).Valid.ShouldBeFalse();

        var run = await VerifyAsync();

        run.Breaks.ShouldHaveSingleItem().Reason.ShouldContain("truncated");
    }

    [Fact]
    public async Task A_persistent_break_is_audited_once_across_nightly_runs()
    {
        var a = await NewTenantAsync("V9", credits: 100);
        await RunRecognitionsAsync(a, seed: 10);
        await TamperWithReasonAsync((await EntryIdsAsync(a))[2]);
        var job = App.Factory.Services.GetServices<IHostedService>().OfType<LedgerVerificationJob>().Single();

        for (var i = 0; i < 3; i++)
        {
            (await job.RunOnceAsync(default))!.Value.BrokenLicenses.ShouldBe(1);
        }

        (await App.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == "ledger.verification_failed"))).ShouldBe(1);
        var record = await App.WithDbAsync(db => db.LedgerBreakRecords.AsNoTracking().SingleAsync());
        record.TimesSeen.ShouldBe(3);
        record.ClearedAt.ShouldBeNull();
    }

    [Fact]
    public async Task The_run_can_be_limited_to_one_license()
    {
        var a = await NewTenantAsync("V10", credits: 100);
        var b = await NewTenantAsync("V10B", credits: 100);
        await RunRecognitionsAsync(a, seed: 10);

        var run = await VerifyAsync(b.LicenseId);

        run.LicenseId.ShouldBe(b.LicenseId);
        run.LicensesChecked.ShouldBe(1);
        (await App.PostAsync(Url, new VerifyLedgerRequest(Guid.NewGuid()), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_nightly_job_runs_the_same_check_in_platform_scope_and_is_listed()
    {
        var a = await NewTenantAsync("V3", credits: 100);
        await RunRecognitionsAsync(a, seed: 10);
        var job = App.Factory.Services.GetServices<IHostedService>().OfType<LedgerVerificationJob>().Single();

        var clean = (await job.RunOnceAsync(default))!.Value;
        clean.LicensesChecked.ShouldBe(1);
        clean.BrokenLicenses.ShouldBe(0);

        await TamperWithReasonAsync((await EntryIdsAsync(a))[2]);
        var broken = (await job.RunOnceAsync(default))!.Value;

        broken.Breaks.Single().LicenseId.ShouldBe(a.LicenseId);
        var runs = await GetAsync<List<LedgerRunDto>>($"{Url}/runs", Platform.AccessToken);
        runs.Count.ShouldBe(2);
        runs.ShouldAllBe(r => r.Trigger == "nightly" && r.Status == "Completed");
    }

    [Fact]
    public async Task Only_one_verification_runs_at_a_time()
    {
        await NewTenantAsync("V4");
        var gate = App.Factory.Services.GetRequiredService<LedgerVerificationGate>();
        gate.TryEnter().ShouldBeTrue();
        try
        {
            (await App.PostAsync(Url, null, Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
        finally
        {
            gate.Exit();
        }

        (await VerifyAsync()).Status.ShouldBe("Completed");
    }

    [Fact]
    public async Task A_verification_running_on_another_node_blocks_this_one_and_releases_cleanly()
    {
        await NewTenantAsync("V6");
        var locks = App.Factory.Services.GetRequiredService<IDistributedLock>();

        var other = await locks.TryAcquireAsync("ledger-verification", default); // stands in for another node's session
        other.ShouldNotBeNull();
        try
        {
            (await locks.TryAcquireAsync("ledger-verification", default)).ShouldBeNull();
            (await App.PostAsync(Url, null, Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
        finally
        {
            await other.DisposeAsync();
        }

        (await VerifyAsync()).Status.ShouldBe("Completed");
    }

    [Fact]
    public async Task The_endpoints_need_authentication_and_the_platform_permission()
    {
        var a = await NewTenantAsync("V5");
        var run = await VerifyAsync();

        (await App.PostAsync(Url, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await App.PostAsync(Url, null, a.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await App.GetAsync($"{Url}/runs/{run.Id}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await App.GetAsync($"{Url}/runs/{run.Id}", a.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await App.GetAsync($"{Url}/runs/{Guid.NewGuid()}", Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_invalid_body_is_a_validation_error()
    {
        (await App.PostAsync(Url, new VerifyLedgerRequest(Guid.Empty), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
