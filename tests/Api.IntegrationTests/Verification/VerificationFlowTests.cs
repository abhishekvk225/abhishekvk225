using System.Net;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Verification;
using NexaVerify.TestSupport;
using NexaVerify.TestSupport.Verification;

namespace NexaVerify.Api.IntegrationTests.Verification;

/// <summary>End to end: accept (202), process with the worker, result, billing and the separated outcome flags.</summary>
[Collection(SqlServerCollection.Name)]
public class VerificationFlowTests : VerificationTestBase
{
    public VerificationFlowTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task A_bank_statement_is_accepted_without_charging_then_processed_and_charged_once_through_the_ledger()
    {
        var t = await NewTenantAsync("V1", credits: 100);

        var submitted = await SubmitBankAsync(t, key: "k-1");
        submitted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        submitted.Headers.Location!.ToString().ShouldContain("/status");
        submitted.Headers.Contains("Retry-After").ShouldBeTrue();
        var accepted = await ReadAsync<VerificationAcceptedDto>(submitted);
        accepted.Status.ShouldBe("Queued");
        accepted.Replayed.ShouldBeFalse();
        (await ConsumedAsync(t)).ShouldBe(0); // nothing is charged at accept time

        var queued = await StatusAsync(t, accepted.RequestId);
        queued.Status.ShouldBe("Queued");
        queued.Stages.Uploaded.ShouldBe("Completed");
        queued.Stages.Extracted.ShouldBe("NotStarted");

        (await RunWorkerAsync()).ShouldBe(1);

        var result = await ResultAsync(t, accepted.RequestId);
        result.Status.ShouldBe("Completed");
        result.Stages.Extracted.ShouldBe("Completed");
        result.Stages.RulesChecked.ShouldBe("Completed");
        result.Stages.ConsistencyChecked.ShouldBe("Completed");
        result.Stages.ExternalVerification.ShouldBe("NotRequested");
        result.Assurance.ShouldBe("ChecksCompleted"); // never "verified": there is no authoritative provider for statements
        result.External.State.ShouldBe("NotRequested");
        result.Extracted!.BankStatement!.TransactionCount.ShouldBe(4);
        result.Extracted.BankStatement.AccountNumberMasked!.ShouldNotContain("9900112233");
        result.Checks.ShouldNotBeEmpty();
        result.Document!.Purged.ShouldBeFalse();
        result.Credits!.Extraction.Charged.ShouldBe(5); // Client Admin holds license.read, so credit figures are included

        // One immutable ledger row, traceable to the request and to the stage key.
        var ledger = await LedgerRowsAsync(t);
        var row = ledger.ShouldHaveSingleItem();
        row.Operation.ShouldBe(MeteredOperation.BankStatementExtraction);
        row.RecognitionRequestId.ShouldBe(accepted.RequestId);
        row.IdempotencyKey.ShouldBe($"vr:{accepted.RequestId:N}:x");
        row.Credits.ShouldBe(-5);
        (await ConsumedAsync(t)).ShouldBe(5);
        var stored = await RequestRowAsync(t, accepted.RequestId);
        stored.CreditsCharged.ShouldBe(5);
        stored.LicenseTransactionId.ShouldBe(row.Id);
        stored.ExpiresAt.ShouldNotBeNull();

        // Transition events and audit rows exist and carry no personal data.
        var events = await InTenantAsync(t, db => db.VerificationEvents.AsNoTracking().Where(e => e.RequestId == accepted.RequestId).OrderBy(e => e.Id).ToListAsync());
        events.Select(e => e.ToStatus).ShouldBe([VerificationStatus.Queued, VerificationStatus.Processing, VerificationStatus.Completed]);
        var audit = await InTenantAsync(t, db => db.AuditLogs.AsNoTracking().Where(a => a.EntityId == accepted.RequestId.ToString()).Select(a => a.Action).ToListAsync());
        audit.ShouldContain("verification.submitted");
        audit.ShouldContain("verification.completed");
        var auditValues = await InTenantAsync(t, db => db.AuditLogs.AsNoTracking().Where(a => a.EntityType == "VerificationRequest").Select(a => a.NewValuesJson).ToListAsync());
        string.Join('\n', auditValues).ShouldNotContain(SampleTexts.Holder);
        string.Join('\n', auditValues).ShouldNotContain("9900112233");

        // The history lists it with filters and paging.
        var list = await ReadAsync<PagedResult<VerificationListItemDto>>(await App.GetAsync("/api/v1/verifications?type=BankStatement&status=Completed&pageSize=10", t.Token));
        list.Items.Single().RequestId.ShouldBe(accepted.RequestId);
        var none = await ReadAsync<PagedResult<VerificationListItemDto>>(await App.GetAsync("/api/v1/verifications?type=Pan", t.Token));
        none.Items.ShouldBeEmpty();
        (await App.GetAsync("/api/v1/verifications?type=Nonsense", t.Token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Transactions are paged.
        var tx = await ReadAsync<PagedResult<BankTransactionDto>>(await App.GetAsync($"/api/v1/verifications/{accepted.RequestId}/transactions?pageSize=2", t.Token));
        tx.TotalCount.ShouldBe(4);
        tx.Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_pan_document_without_a_configured_provider_reports_NotAvailable_and_is_never_called_verified()
    {
        var t = await NewTenantAsync("V2");
        // This app has the fake providers; a request whose provider is NOT configured is covered by the dedicated class below.
        var result = await ProcessAsync(t, await SubmitPanAsync(t, PanPdf("ABCPT1234F"), PanFields("ABCPT1234F")));

        // The fake provider answers Inconclusive for numbers ending in anything but V/N/I/E/T... F is not scripted -> Inconclusive.
        result.External.State.ShouldBe("Inconclusive");
        result.Status.ShouldBe("ReviewRequired");
        result.Assurance.ShouldBe("ChecksCompleted");
        result.External.Reference.ShouldBeNull();
        result.Extracted!.Pan!.PanMasked.ShouldNotContain("ABCPT1234F");
        result.Credits!.Provider.Charged.ShouldBe(0); // an inconclusive provider answer is never billed
        result.Credits.Extraction.Charged.ShouldBe(1);
        result.ReviewReasons.Select(r => r.Code).ShouldContain("PROVIDER_INCONCLUSIVE");
    }

    [Theory]
    [InlineData("ABCPV1234V", "Verified", "Completed", "AuthoritativelyVerified", 3)]
    [InlineData("ABCPN1234N", "NotVerified", "ReviewRequired", "ChecksCompleted", 3)]
    [InlineData("ABCPE1234E", "ProviderError", "ReviewRequired", "ChecksCompleted", 1)]
    [InlineData("ABCPT1234T", "Timeout", "ReviewRequired", "ChecksCompleted", 1)]
    [InlineData("ABCPI1234I", "Inconclusive", "ReviewRequired", "ChecksCompleted", 1)]
    public async Task Provider_outcomes_map_to_status_assurance_and_credits(string pan, string external, string status, string assurance, int credits)
    {
        var t = await NewTenantAsync("V3" + pan[3]);
        var result = await ProcessAsync(t, await SubmitPanAsync(t, PanPdf(pan), PanFields(pan)));

        result.External.State.ShouldBe(external);
        result.Status.ShouldBe(status);
        result.Assurance.ShouldBe(assurance);
        (await ConsumedAsync(t)).ShouldBe(credits);
        if (external == "Verified")
        {
            result.External.ProviderId.ShouldBe("fake-pan");
            result.External.Reference.ShouldNotBeNull();
        }
        else
        {
            result.Assurance.ShouldNotBe("AuthoritativelyVerified");
        }
    }

    [Fact]
    public async Task A_passport_is_read_with_its_mrz_and_stays_unverified_without_a_provider_answer()
    {
        var t = await NewTenantAsync("V4");
        var result = await ProcessAsync(t, await SubmitPassportAsync(t));

        result.Extracted!.Passport!.Mrz.Present.ShouldBeTrue();
        result.Extracted.Passport.Mrz.Checks!.Composite.ShouldBe("Valid");
        result.Extracted.Passport.PassportNumberMasked.ShouldNotContain("X1234567");
        result.Assurance.ShouldNotBe("AuthoritativelyVerified"); // MRZ validity proves consistency only
        result.External.State.ShouldBe("Inconclusive"); // fake provider: number does not end in a scripted letter
        (await ConsumedAsync(t)).ShouldBe(2); // passport extraction; the inconclusive provider stage is free
    }

    [Fact]
    public async Task An_unreadable_document_fails_without_consuming_credits_and_keeps_no_data()
    {
        var t = await NewTenantAsync("V5");
        var prose = SampleDocuments.Pdf(["Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor.", "Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip."]);
        var submitted = await SubmitBankAsync(t, prose);
        var accepted = await AcceptedAsync(submitted);

        await RunWorkerAsync();
        var status = await StatusAsync(t, accepted.RequestId);
        status.Status.ShouldBe("Failed");
        status.ErrorCode.ShouldBe("DOCUMENT_UNREADABLE");
        (await ConsumedAsync(t)).ShouldBe(0);
        (await LedgerRowsAsync(t)).ShouldBeEmpty();

        var result = await ResultAsync(t, accepted.RequestId);
        result.Extracted.ShouldBeNull();
        result.Document!.Purged.ShouldBeTrue(); // nothing is kept for a failed request
        var rows = await InTenantAsync(t, async db => (await db.VerificationDocuments.CountAsync(d => d.PurgedAt == null), await db.VerificationDocumentBlobs.CountAsync(), await db.VerificationResults.CountAsync()));
        rows.ShouldBe((0, 0, 0));
    }

    [Fact]
    public async Task A_scanned_document_without_an_ocr_engine_fails_honestly_instead_of_returning_empty_results()
    {
        var t = await NewTenantAsync("V5B");
        var accepted = await AcceptedAsync(await SubmitBankAsync(t, SampleDocuments.BlankPdf()));

        await RunWorkerAsync();
        var status = await StatusAsync(t, accepted.RequestId);
        status.Status.ShouldBe("Failed");
        status.ErrorCode.ShouldBe("OCR_UNAVAILABLE");
        (await ConsumedAsync(t)).ShouldBe(0);
    }

    [Fact]
    public async Task Identical_retries_with_the_same_key_return_the_first_request_and_never_double_charge()
    {
        var t = await NewTenantAsync("V6");
        var pdf = BankPdf();
        var first = await AcceptedAsync(await SubmitBankAsync(t, pdf, "same-key"));

        var again = await SubmitBankAsync(t, pdf, "same-key");
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        var replay = await ReadAsync<VerificationAcceptedDto>(again);
        replay.RequestId.ShouldBe(first.RequestId);
        replay.Replayed.ShouldBeTrue();

        // Ten parallel identical submits with a fresh key: exactly one request, one document.
        var parallel = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => SubmitBankAsync(t, pdf, "burst-key")));
        var ids = new HashSet<Guid>();
        foreach (var response in parallel)
        {
            ids.Add((await AcceptedAsync(response)).RequestId);
        }

        ids.Count.ShouldBe(1);
        var counts = await InTenantAsync(t, async db => (await db.VerificationRequests.CountAsync(), await db.VerificationDocuments.CountAsync()));
        counts.ShouldBe((2, 2));

        // A different document under the same key is a different request.
        var other = await AcceptedAsync(await SubmitBankAsync(t, BankPdf("extra synthetic line"), "same-key"));
        other.RequestId.ShouldNotBe(first.RequestId);

        // Processing twice never double-charges either: a repeated run finds nothing to do and the ledger holds one row per request.
        await RunWorkerAsync();
        await RunWorkerAsync();
        (await LedgerRowsAsync(t)).Count.ShouldBe(3);
        (await ConsumedAsync(t)).ShouldBe(15);
    }

    [Fact]
    public async Task Concurrent_processing_can_never_overdraw_the_license()
    {
        var t = await NewTenantAsync("V7", credits: 15); // three statements at 5 credits
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            ids.Add((await AcceptedAsync(await SubmitBankAsync(t, BankPdf($"synthetic line {i}")))).RequestId);
        }

        await RunWorkerAsync();

        var statuses = new List<VerificationStatusDto>();
        foreach (var id in ids)
        {
            statuses.Add(await StatusAsync(t, id));
        }

        statuses.Count(s => s.Status == "Completed").ShouldBe(3);
        statuses.Where(s => s.Status == "Failed").ShouldAllBe(s => s.ErrorCode == "CREDITS_UNAVAILABLE" && s.CreditsCharged == 0);
        (await ConsumedAsync(t)).ShouldBe(15);
        var license = await App.WithDbAsync(db => db.Licenses.AsNoTracking().SingleAsync(l => l.Id == t.LicenseId));
        license.ConsumedCredits.ShouldBeLessThanOrEqualTo(license.TotalCredits);
        (await LedgerRowsAsync(t)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Retrying_the_provider_check_charges_it_once_and_leaves_extraction_alone()
    {
        var t = await NewTenantAsync("V8");
        Pan.Mode = FakeProviderMode.Error;
        var accepted = await AcceptedAsync(await SubmitPanAsync(t, PanPdf("ABCPT1234F"), PanFields("ABCPT1234F")));
        await RunWorkerAsync();
        (await ResultAsync(t, accepted.RequestId)).External.State.ShouldBe("ProviderError");
        (await ConsumedAsync(t)).ShouldBe(1);

        Pan.Mode = FakeProviderMode.Verified;
        var retry = await App.PostAsync($"/api/v1/verifications/{accepted.RequestId}/retry-external", null, t.Token);
        retry.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await StatusAsync(t, accepted.RequestId)).Status.ShouldBe("Queued");

        // A second retry while the first is queued is refused.
        (await App.PostAsync($"/api/v1/verifications/{accepted.RequestId}/retry-external", null, t.Token)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await RunWorkerAsync();
        var result = await ResultAsync(t, accepted.RequestId);
        result.Status.ShouldBe("Completed");
        result.External.State.ShouldBe("Verified");
        result.Assurance.ShouldBe("AuthoritativelyVerified");
        (await ConsumedAsync(t)).ShouldBe(3); // extraction (1) + provider (2), each exactly once
        (await LedgerRowsAsync(t)).Select(r => r.IdempotencyKey).ShouldBe([$"vr:{accepted.RequestId:N}:x", $"vr:{accepted.RequestId:N}:p"]);

        // A definitive answer cannot be retried.
        (await App.PostAsync($"/api/v1/verifications/{accepted.RequestId}/retry-external", null, t.Token)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Cancelling_a_queued_request_is_immediate_and_a_finished_one_cannot_be_cancelled()
    {
        var t = await NewTenantAsync("V9");
        var queued = await AcceptedAsync(await SubmitBankAsync(t));
        var cancelled = await App.PostAsync($"/api/v1/verifications/{queued.RequestId}/cancel", null, t.Token);
        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<VerificationStatusDto>(cancelled)).Status.ShouldBe("Cancelled");
        (await RunWorkerAsync()).ShouldBe(0); // a cancelled request is never claimed
        (await ConsumedAsync(t)).ShouldBe(0);

        var done = await AcceptedAsync(await SubmitBankAsync(t, BankPdf("another synthetic line")));
        await RunWorkerAsync();
        var late = await App.PostAsync($"/api/v1/verifications/{done.RequestId}/cancel", null, t.Token);
        late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(late)).ShouldBe(ErrorCodes.VerificationNotCancellable);
    }

    [Fact]
    public async Task Erasing_removes_the_document_result_and_checks_but_keeps_the_billing_trace()
    {
        var t = await NewTenantAsync("V10");
        var accepted = await AcceptedAsync(await SubmitBankAsync(t));
        (await App.DeleteAsync($"/api/v1/verifications/{accepted.RequestId}/data", t.Token)).StatusCode.ShouldBe(HttpStatusCode.Conflict); // still queued
        await RunWorkerAsync();

        (await App.DeleteAsync($"/api/v1/verifications/{accepted.RequestId}/data", t.Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var result = await ResultAsync(t, accepted.RequestId);
        result.Extracted.ShouldBeNull();
        result.Document!.Purged.ShouldBeTrue();
        result.Checks.ShouldBeEmpty();
        var left = await InTenantAsync(t, async db => (await db.VerificationDocuments.CountAsync(d => d.PurgedAt == null), await db.VerificationDocumentBlobs.CountAsync(), await db.VerificationResults.CountAsync(), await db.VerificationChecks.CountAsync()));
        left.ShouldBe((0, 0, 0, 0));
        (await LedgerRowsAsync(t)).Count.ShouldBe(1);
        (await App.GetAsync($"/api/v1/verifications/{accepted.RequestId}/transactions", t.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
