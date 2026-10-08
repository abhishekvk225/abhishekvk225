using System.Net;
using System.Net.Http.Json;
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

/// <summary>The platform-wide ledger tamper check: endpoint, nightly job, permissions and a real (trigger-bypassing) tamper.</summary>
[Collection(SqlServerCollection.Name)]
public class LedgerVerificationApiTests : UsageTestBase
{
    private const string Url = "/api/v1/admin/licensing/verify-ledger";

    public LedgerVerificationApiTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    private async Task<LedgerVerificationReportDto> VerifyAsync()
    {
        var response = await App.PostAsync(Url, null, Platform.AccessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<LedgerVerificationReportDto>(AuthApp.Json))!;
    }

    /// <summary>Bypasses the append-only trigger the way a DBA with raw access could, then restores it. Used only to prove the check detects it.</summary>
    private Task TamperWithReasonAsync(long entryId) =>
        App.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE licensing.LicenseTransactions DISABLE TRIGGER trg_LicenseTransactions_AppendOnly");
            try
            {
                (await db.Database.ExecuteSqlAsync($"UPDATE licensing.LicenseTransactions SET Reason = N'forged' WHERE Id = {entryId}")).ShouldBe(1);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE licensing.LicenseTransactions ENABLE TRIGGER trg_LicenseTransactions_AppendOnly");
            }

            return true;
        });

    [Fact]
    public async Task An_untouched_platform_verifies_clean()
    {
        var a = await NewTenantAsync("V1", credits: 100);
        var b = await NewTenantAsync("V1B", credits: 100);
        await RunRecognitionsAsync(a, seed: 10);
        await RunRecognitionsAsync(b, seed: 70);

        var report = await VerifyAsync();

        report.LicensesChecked.ShouldBe(2);
        report.EntriesChecked.ShouldBe(2 * (1 + 5)); // a grant and five charges each
        report.BrokenLicenses.ShouldBe(0);
        report.Breaks.ShouldBeEmpty();
        (await App.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == "ledger.verification_failed"))).ShouldBe(0);
    }

    [Fact]
    public async Task A_real_tamper_is_found_pinned_to_its_row_and_audited()
    {
        var a = await NewTenantAsync("V2", credits: 100);
        var b = await NewTenantAsync("V2B", credits: 100);
        await RunRecognitionsAsync(a, seed: 10);
        await RunRecognitionsAsync(b, seed: 70);
        var rows = await App.WithTenantDbAsync(a.ClientId, db => db.LicenseTransactions.AsNoTracking().OrderBy(t => t.Id).Select(t => t.Id).ToListAsync());
        var victim = rows[3];
        await TamperWithReasonAsync(victim);

        var report = await VerifyAsync();

        report.BrokenLicenses.ShouldBe(1);
        var broken = report.Breaks.Single();
        (broken.LicenseId, broken.ClientId, broken.FirstBrokenEntryId).ShouldBe((a.LicenseId, a.ClientId, victim));
        broken.Reason.ShouldContain("does not match its hash");
        var audit = await App.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(l => l.Action == "ledger.verification_failed").ToListAsync());
        var entry = audit.ShouldHaveSingleItem();
        entry.ClientId.ShouldBe(a.ClientId);
        entry.EntityId.ShouldBe(a.LicenseId.ToString());

        // the per-license check agrees, and the other client is unaffected
        (await GetAsync<LedgerVerificationDto>($"/api/v1/admin/licenses/{a.LicenseId}/verify-ledger", Platform.AccessToken)).Valid.ShouldBeFalse();
        (await GetAsync<LedgerVerificationDto>($"/api/v1/admin/licenses/{b.LicenseId}/verify-ledger", Platform.AccessToken)).Valid.ShouldBeTrue();
    }

    [Fact]
    public async Task The_nightly_job_runs_the_same_check_in_platform_scope()
    {
        var a = await NewTenantAsync("V3", credits: 100);
        await RunRecognitionsAsync(a, seed: 10);
        var job = App.Factory.Services.GetServices<IHostedService>().OfType<LedgerVerificationJob>().Single();

        var clean = (await job.RunOnceAsync(default))!.Value;
        clean.LicensesChecked.ShouldBe(1);
        clean.BrokenLicenses.ShouldBe(0);

        await TamperWithReasonAsync((await App.WithTenantDbAsync(a.ClientId, db => db.LicenseTransactions.AsNoTracking().OrderBy(t => t.Id).Select(t => t.Id).ToListAsync()))[2]);
        var broken = (await job.RunOnceAsync(default))!.Value;

        broken.Breaks.Single().LicenseId.ShouldBe(a.LicenseId);
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

        (await App.PostAsync(Url, null, Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_endpoint_needs_authentication_and_the_platform_permission()
    {
        var a = await NewTenantAsync("V5");

        (await App.PostAsync(Url, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await App.PostAsync(Url, null, a.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
