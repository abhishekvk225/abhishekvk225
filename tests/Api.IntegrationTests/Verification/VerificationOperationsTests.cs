using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Verification;
using NexaVerify.TestSupport;
using NexaVerify.TestSupport.Verification;

namespace NexaVerify.Api.IntegrationTests.Verification;

/// <summary>Worker recovery (leases, attempt cap, expiry), re-claims without double charging, and the retention job, on real SQL Server.</summary>
[Collection(SqlServerCollection.Name)]
public class VerificationOperationsTests : VerificationTestBase
{
    public VerificationOperationsTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>Takes the lease the way a worker would, then lets it lapse: a crashed node.</summary>
    private async Task CrashAfterClaimAsync(Tenant t)
    {
        var claimed = await App.WithServicesAsync(null, sp => sp.GetRequiredService<IVerificationQueue>()
            .ClaimDueAsync(10, 10, DateTime.UtcNow, TimeSpan.FromMinutes(5), Guid.NewGuid(), CancellationToken.None));
        claimed.ShouldNotBeEmpty();
        await App.WithDbAsync(db => db.VerificationRequests.Where(r => r.ClientId == t.ClientId && r.Status == VerificationStatus.Processing)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LeaseUntil, DateTime.UtcNow.AddMinutes(-1))));
    }

    [Fact]
    public async Task A_crashed_workers_request_is_requeued_then_failed_at_the_attempt_cap_without_being_charged()
    {
        var t = await NewTenantAsync("O1");
        var id = (await AcceptedAsync(await SubmitBankAsync(t))).RequestId;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await CrashAfterClaimAsync(t);
            await RecoverAsync();
            var row = await RequestRowAsync(t, id);
            row.Status.ShouldBe(VerificationStatus.Queued);
            row.Attempts.ShouldBe(attempt);
            row.LeaseToken.ShouldBeNull();
        }

        await CrashAfterClaimAsync(t); // third attempt: the cap (3) is reached
        await RecoverAsync();
        var failed = await StatusAsync(t, id);
        failed.Status.ShouldBe("Failed");
        failed.ErrorCode.ShouldBe("PROCESSING_TIMEOUT");
        (await ConsumedAsync(t)).ShouldBe(0);
        var notes = await InTenantAsync(t, db => db.VerificationEvents.AsNoTracking().Where(e => e.RequestId == id).Select(e => e.NoteCode).ToListAsync());
        notes.Count(n => n == "LEASE_EXPIRED").ShouldBe(2);
        notes.ShouldContain("PROCESSING_TIMEOUT");
        (await RunWorkerAsync()).ShouldBe(0); // a poison request is not claimed again
    }

    [Fact]
    public async Task A_request_recovered_after_a_crash_completes_normally_and_is_charged_once()
    {
        var t = await NewTenantAsync("O2");
        var id = (await AcceptedAsync(await SubmitBankAsync(t))).RequestId;
        await CrashAfterClaimAsync(t);
        await RecoverAsync();

        await RunWorkerAsync();
        (await StatusAsync(t, id)).Status.ShouldBe("Completed");
        (await LedgerRowsAsync(t)).Count.ShouldBe(1);
        (await ConsumedAsync(t)).ShouldBe(5);
        (await RequestRowAsync(t, id)).Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task A_crash_between_the_extraction_commit_and_the_provider_call_resumes_at_the_provider_stage_without_recharging()
    {
        var t = await NewTenantAsync("O3");
        Pan.Mode = FakeProviderMode.Error;
        var id = (await AcceptedAsync(await SubmitPanAsync(t, PanPdf("ABCPV1234V"), PanFields("ABCPV1234V")))).RequestId;
        await RunWorkerAsync();
        (await ConsumedAsync(t)).ShouldBe(1); // extraction only; the provider answered ProviderError

        // Rewind to "extraction committed, provider call not yet recorded, node died": Processing under a lapsed lease.
        await App.WithDbAsync(db => db.VerificationRequests.Where(r => r.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, VerificationStatus.Processing).SetProperty(r => r.LeaseToken, Guid.NewGuid()).SetProperty(r => r.LeaseUntil, DateTime.UtcNow.AddMinutes(-1))
            .SetProperty(r => r.CompletedAt, (DateTime?)null).SetProperty(r => r.ExternalState, ExternalState.NotRequested).SetProperty(r => r.ExternalProviderId, (string?)null)));
        await RecoverAsync();
        Pan.Mode = FakeProviderMode.Verified;

        await RunWorkerAsync();
        var result = await ResultAsync(t, id);
        result.External.State.ShouldBe("Verified");
        (await LedgerRowsAsync(t)).Select(r => r.IdempotencyKey).ShouldBe([$"vr:{id:N}:x", $"vr:{id:N}:p"]);
        (await ConsumedAsync(t)).ShouldBe(3);
    }

    [Fact]
    public async Task Old_unprocessed_requests_expire_unbilled_but_a_requeued_provider_retry_never_does()
    {
        var t = await NewTenantAsync("O4");
        var stale = (await AcceptedAsync(await SubmitBankAsync(t))).RequestId;
        Pan.Mode = FakeProviderMode.Error;
        var retried = (await AcceptedAsync(await SubmitPanAsync(t, PanPdf("ABCPV1234V"), PanFields("ABCPV1234V")))).RequestId;
        await App.WithDbAsync(db => db.VerificationRequests.Where(r => r.Id == stale).ExecuteUpdateAsync(s => s.SetProperty(r => r.NextAttemptAt, DateTime.UtcNow.AddHours(1))));
        await RunWorkerAsync(); // processes the PAN only (the statement is not due yet)
        (await App.PostAsync($"/api/v1/verifications/{retried}/retry-external", null, t.Token)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await App.WithDbAsync(db => db.VerificationRequests.Where(r => r.ClientId == t.ClientId).ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedAt, DateTime.UtcNow.AddHours(-3))));
        await RecoverAsync();

        var expired = await StatusAsync(t, stale);
        expired.Status.ShouldBe("Expired");
        expired.ErrorCode.ShouldBe("EXPIRED");
        (await StatusAsync(t, retried)).Status.ShouldBe("Queued"); // already delivered work is not expired
        (await LedgerRowsAsync(t)).ShouldHaveSingleItem(); // only the PAN extraction
    }

    [Fact]
    public async Task The_retention_job_purges_by_table_blanks_personal_metadata_and_keeps_the_billing_trace()
    {
        var t = await NewTenantAsync("O5");
        var other = await NewTenantAsync("O5B");
        var due = (await AcceptedAsync(await SubmitBankAsync(t))).RequestId;
        var fresh = (await AcceptedAsync(await SubmitBankAsync(t, BankPdf("synthetic line two")))).RequestId;
        var othersRequest = (await AcceptedAsync(await SubmitBankAsync(other))).RequestId;
        await RunWorkerAsync();
        var link = await ReadAsync<DownloadLinkDto>(await App.PostAsync($"/api/v1/verifications/{due}/downloads", new VerificationDownloadRequest("Report", "json"), t.Token));
        link.ShouldNotBeNull();

        var past = DateTime.UtcNow.AddDays(-1);
        await InTenantAsync(t, async db =>
        {
            await db.VerificationDocuments.Where(d => d.RequestId == due).ExecuteUpdateAsync(s => s.SetProperty(d => d.PurgeAfter, past));
            await db.VerificationResults.Where(r => r.RequestId == due).ExecuteUpdateAsync(s => s.SetProperty(r => r.RetainUntil, past));
            await db.VerificationDownloadGrants.ExecuteUpdateAsync(s => s.SetProperty(g => g.ExpiresAt, DateTime.UtcNow.AddDays(-3)));
            return 0;
        });
        await App.WithDbAsync(async db =>
        {
            await db.VerificationRequests.Where(r => r.Id == due).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, past).SetProperty(r => r.CreatedAt, DateTime.UtcNow.AddDays(-100)).SetProperty(r => r.IpAddress, "203.0.113.5"));
            return 0;
        });

        var swept = await SweepRetentionAsync();
        swept.Documents.ShouldBe(1);
        swept.Results.ShouldBeGreaterThanOrEqualTo(0);
        swept.Requests.ShouldBe(1);
        swept.Grants.ShouldBe(1);
        swept.IpAddresses.ShouldBe(1);

        var gone = await ResultAsync(t, due);
        gone.Extracted.ShouldBeNull();
        gone.Document!.Purged.ShouldBeTrue();
        (await RequestRowAsync(t, due)).PurgedAt.ShouldNotBeNull();
        (await RequestRowAsync(t, due)).IpAddress.ShouldBeNull();
        (await LedgerRowsAsync(t)).Count.ShouldBe(2); // billing trace untouched
        var kept = await InTenantAsync(t, async db => (await db.VerificationEvents.CountAsync(e => e.RequestId == due)));
        kept.ShouldBe(3);

        // Nothing else was touched: the other request of the same tenant and the other tenant's request keep their data.
        (await ResultAsync(t, fresh)).Extracted.ShouldNotBeNull();
        (await ResultAsync(other, othersRequest)).Extracted.ShouldNotBeNull();
        (await InTenantAsync(other, db => db.VerificationDocuments.CountAsync(d => d.PurgedAt == null))).ShouldBe(1);

        // Running it again finds nothing.
        var again = await SweepRetentionAsync();
        (again.Documents, again.Requests, again.Grants).ShouldBe((0, 0, 0));
    }

    [Fact]
    public async Task Requests_that_cannot_be_afforded_are_refused_before_anything_is_stored()
    {
        var broke = await NewTenantAsync("O6", credits: 2);
        (await SubmitBankAsync(broke)).StatusCode.ShouldBe(HttpStatusCode.PaymentRequired); // statements cost 5

        // PAN with a configured provider needs extraction (1) + provider (2); two credits are not enough, three are.
        var response = await SubmitPanAsync(broke, PanPdf());
        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        (await CodeOf(response)).ShouldBe(ErrorCodes.LicenseInsufficientBalance);
        (await InTenantAsync(broke, db => db.VerificationRequests.CountAsync())).ShouldBe(0);
        (await InTenantAsync(broke, db => db.VerificationDocuments.CountAsync())).ShouldBe(0);

        var none = await NewTenantAsync("O6B", credits: 0);
        var noLicense = await SubmitPassportAsync(none);
        noLicense.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
    }
}

[Collection(SqlServerCollection.Name)]
public class VerificationQueueCapTests : VerificationTestBase
{
    public VerificationQueueCapTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    protected override IReadOnlyDictionary<string, string> ExtraSettings => new Dictionary<string, string> { ["Verification:Limits:MaxQueuedPerClient"] = "2" };

    [Fact]
    public async Task A_client_cannot_queue_more_than_the_configured_cap()
    {
        var t = await NewTenantAsync("Q1");
        (await SubmitBankAsync(t, BankPdf("one"))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await SubmitBankAsync(t, BankPdf("two"))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var over = await SubmitBankAsync(t, BankPdf("three"));
        over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await CodeOf(over)).ShouldBe(ErrorCodes.VerificationQueueFull);

        await RunWorkerAsync();
        (await SubmitBankAsync(t, BankPdf("four"))).StatusCode.ShouldBe(HttpStatusCode.Accepted); // room again once the queue drained
    }
}

[Collection(SqlServerCollection.Name)]
public class VerificationThrottleTests : VerificationTestBase
{
    public VerificationThrottleTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    protected override IReadOnlyDictionary<string, string> ExtraSettings => new Dictionary<string, string> { ["Throttle:VerificationSubmitsPerMinute"] = "2" };

    [Fact]
    public async Task Submissions_are_throttled_per_principal()
    {
        var a = await NewTenantAsync("T1");
        var b = await NewTenantAsync("T1B");
        (await SubmitBankAsync(a, BankPdf("one"))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await SubmitBankAsync(a, BankPdf("two"))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var limited = await SubmitBankAsync(a, BankPdf("three"));
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.Contains("Retry-After").ShouldBeTrue();
        (await CodeOf(limited)).ShouldBe(ErrorCodes.RateLimited);
        (await InTenantAsync(a, db => db.VerificationRequests.CountAsync())).ShouldBe(2);

        (await SubmitBankAsync(b, BankPdf("one"))).StatusCode.ShouldBe(HttpStatusCode.Accepted); // another principal is unaffected
    }
}

[Collection(SqlServerCollection.Name)]
public class VerificationUploadLimitTests : VerificationTestBase
{
    public VerificationUploadLimitTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    protected override IReadOnlyDictionary<string, string> ExtraSettings => new Dictionary<string, string> { ["Verification:Upload:MaxFileBytes"] = "30000" };

    [Fact]
    public async Task A_file_over_the_configured_size_is_refused_with_413_and_nothing_is_stored()
    {
        var t = await NewTenantAsync("U1");
        var big = new byte[40_000];
        big[0] = 0x25; // '%'
        var response = await PostDocumentAsync("/api/v1/verifications/bank-statements", t.Token, big, BankFields());
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await CodeOf(response)).ShouldBe(ErrorCodes.DocumentTooLarge);
        (await InTenantAsync(t, db => db.VerificationRequests.CountAsync())).ShouldBe(0);

        var modules = await ReadAsync<List<VerificationModuleDto>>(await App.GetAsync("/api/v1/verifications/modules", t.Token));
        modules.Single(m => m.Key == "pan").Limits!.MaxFileBytes.ShouldBe(30000);
        (await SubmitBankAsync(t)).StatusCode.ShouldBe(HttpStatusCode.Accepted); // a normal statement still fits
    }
}

[Collection(SqlServerCollection.Name)]
public class VerificationOriginalDownloadTests : VerificationTestBase
{
    public VerificationOriginalDownloadTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    protected override IReadOnlyDictionary<string, string> ExtraSettings => new Dictionary<string, string> { ["Verification:Download:AllowOriginalDownload"] = "true" };

    [Fact]
    public async Task When_enabled_the_original_comes_back_decrypted_with_the_detected_type_and_is_audited()
    {
        var t = await NewTenantAsync("OD1");
        var pdf = BankPdf();
        var id = (await AcceptedAsync(await SubmitBankAsync(t, pdf))).RequestId;
        await RunWorkerAsync();

        var link = await ReadAsync<DownloadLinkDto>(await App.PostAsync($"/api/v1/verifications/{id}/downloads", new VerificationDownloadRequest("OriginalDocument", null), t.Token));
        var file = await App.GetAsync(link.DownloadUrl, t.Token);
        file.StatusCode.ShouldBe(HttpStatusCode.OK);
        file.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        file.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        (await file.Content.ReadAsByteArrayAsync()).ShouldBe(pdf);
        (await InTenantAsync(t, db => db.AuditLogs.CountAsync(a => a.Action == "verification.document_downloaded"))).ShouldBe(1);

        // The ciphertext at rest is not the file.
        var stored = await InTenantAsync(t, db => db.VerificationDocumentBlobs.AsNoTracking().Select(b => b.Content).SingleAsync());
        stored.ShouldNotBe(pdf);
        stored.AsSpan().IndexOf("%PDF"u8).ShouldBe(-1);
    }
}

/// <summary>The product default: no authoritative provider is configured, so nothing can ever be reported as officially verified.</summary>
[Collection(SqlServerCollection.Name)]
public class VerificationUnconfiguredProviderTests : VerificationTestBase
{
    public VerificationUnconfiguredProviderTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    protected override bool UseFakeProviders => false;

    [Fact]
    public async Task Without_a_provider_the_checks_complete_but_nothing_is_called_verified_and_nothing_extra_is_charged()
    {
        var t = await NewTenantAsync("N1");
        var result = await ProcessAsync(t, await SubmitPanAsync(t, PanPdf("ABCPV1234V"), PanFields("ABCPV1234V")));

        result.Stages.ExternalVerification.ShouldBe("ProviderNotConfigured");
        result.External.State.ShouldBe("ProviderNotConfigured");
        result.External.ProviderId.ShouldBeNull();
        result.External.Reference.ShouldBeNull();
        result.External.Message!.ShouldContain("not available");
        result.Assurance.ShouldBe("ChecksCompleted");
        result.Status.ShouldBe("Completed"); // not being able to verify officially is not a review reason by itself
        result.Checks.ShouldContain(c => c.Code == "EXTERNAL_NOT_PERFORMED" && c.Stage == "External");
        result.Credits!.Extraction.Charged.ShouldBe(1);
        result.Credits.Provider.Charged.ShouldBe(0);
        (await ConsumedAsync(t)).ShouldBe(1);

        var again = await App.PostAsync($"/api/v1/verifications/{result.RequestId}/retry-external", null, t.Token);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(again)).ShouldBe(ErrorCodes.VerificationNotRetryable);

        // Credits needed at accept time cover the extraction stage only, because no provider stage can happen.
        var small = await NewTenantAsync("N1B", credits: 1);
        (await SubmitPanAsync(small, PanPdf())).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var modules = await ReadAsync<List<VerificationModuleDto>>(await App.GetAsync("/api/v1/verifications/modules", t.Token));
        modules.Single(m => m.Key == "pan").ProviderConfigured.ShouldBe(false);
        modules.Single(m => m.Key == "bank-statement").ProviderConfigured.ShouldBeNull();
        var admin = await ReadAsync<List<AdminVerificationModuleDto>>(await App.GetAsync("/api/v1/admin/verifications/modules", Platform.AccessToken));
        admin.Single(m => m.Key == "passport").Providers.Single().Configured.ShouldBeFalse();
    }

    [Fact]
    public async Task A_passport_without_a_provider_is_never_authoritatively_verified_even_with_a_valid_mrz()
    {
        var t = await NewTenantAsync("N2");
        var result = await ProcessAsync(t, await SubmitPassportAsync(t));
        result.Extracted!.Passport!.Mrz.Checks!.Composite.ShouldBe("Valid");
        result.External.State.ShouldBe("ProviderNotConfigured");
        result.Assurance.ShouldBe("ChecksCompleted");
        result.Assurance.ShouldNotBe("AuthoritativelyVerified");
    }
}
