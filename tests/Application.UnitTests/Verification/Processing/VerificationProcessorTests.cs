using NexaVerify.Application.Common;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Submission;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.UnitTests.Verification.Processing;

/// <summary>The worker-side logic with in-memory ports: stages, leases, retries, credit rules and Rule V-1.</summary>
public class VerificationProcessorTests
{
    private static ProcessorRig Pan(bool configured = false)
    {
        var rig = new ProcessorRig(VerificationType.Pan) { };
        rig.Catalog.Configured = configured;
        return rig;
    }

    private static async Task<SubmissionEnvelope> EnvelopeAsync(ProcessorRig rig) =>
        (await rig.Codec.OpenAsync(rig.ClientId, rig.Request.Id, rig.Results.Rows[rig.Request.Id].DataEnc, CancellationToken.None))!;

    [Fact]
    public async Task A_completed_pipeline_stores_the_sealed_result_charges_extraction_once_and_announces_completion_without_personal_data()
    {
        var rig = new ProcessorRig(VerificationType.BankStatement);
        rig.Module.Document = (_, _) => Task.FromResult(ScriptedModule.DefaultDocument(
            new BankStatementData { AccountNumberMasked = "XXXXXXX233", BankName = "Synthetic Bank", AccountHolderName = ProcessorRig.CanaryName }));

        await rig.RunAsync();

        rig.Request.Status.ShouldBe(VerificationStatus.Completed);
        rig.Request.Assurance.ShouldBe(AssuranceLevel.ChecksCompleted);
        rig.Request.CreditsCharged.ShouldBe(5);
        rig.Request.LicenseTransactionId.ShouldBe(1);
        rig.Request.ExpiresAt.ShouldNotBeNull();
        rig.Meter.Charges.ShouldBe([(VerificationStage.Extraction, MeterOutcome.Success, rig.Request.Id)]);
        rig.Module.ExternalCalls.ShouldBe(0); // statements have no provider stage

        // The extracted data is only in the sealed row, together with the submitted fields.
        var row = rig.Results.Rows[rig.Request.Id];
        row.IdentifierMasked.ShouldNotBeNull();
        row.IdentifierMasked.ShouldNotContain("2233");
        (await EnvelopeAsync(rig)).Extracted.ShouldBeOfType<BankStatementData>().BankName.ShouldBe("Synthetic Bank");
        rig.Results.Checks.Select(c => c.Code).ShouldBe(["PAN_FORMAT", "PAN_NAME_MATCH"]);

        // Audit and webhook carry ids, status and credit numbers only.
        rig.Audit.Entries.Select(e => e.Action).ShouldBe(["verification.completed"]);
        var webhook = rig.Webhooks.Published.ShouldHaveSingleItem();
        webhook.Event.ShouldBe("verification.completed");
        var all = System.Text.Json.JsonSerializer.Serialize(webhook.Data) + System.Text.Json.JsonSerializer.Serialize(rig.Audit.Entries[0].NewValues);
        all.ShouldNotContain(ProcessorRig.CanaryName);
        all.ShouldNotContain("XXXXXXX233");
        all.ShouldContain("\"creditsCharged\":5");
    }

    [Fact]
    public async Task A_refused_charge_fails_the_request_erases_its_data_and_delivers_nothing_for_free()
    {
        var rig = Pan();
        rig.Meter.ChargeErrors[VerificationStage.Extraction] = Error.PaymentRequired("LICENSE_INSUFFICIENT_BALANCE", "no");

        await rig.RunAsync();

        rig.Request.Status.ShouldBe(VerificationStatus.Failed);
        rig.Request.ErrorCode.ShouldBe("CREDITS_UNAVAILABLE");
        rig.Request.CreditsCharged.ShouldBe(0);
        rig.Retention.Erased.ShouldBe([rig.Request.Id]);
        rig.Results.ReplaceResultCalls.ShouldBe(0); // the extracted data was never stored
        rig.Meter.Charges.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_credits_at_the_start_fail_the_request_before_any_work_is_done()
    {
        var rig = Pan();
        rig.Meter.PreflightError = Error.PaymentRequired("LICENSE_EXPIRED", "expired");

        await rig.RunAsync();

        rig.Request.ErrorCode.ShouldBe("CREDITS_UNAVAILABLE");
        rig.Module.DocumentCalls.ShouldBe(0);
        rig.Retention.Erased.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task An_unreadable_document_fails_with_the_modules_code_and_is_only_charged_under_an_attempt_policy()
    {
        var rig = Pan();
        rig.Module.Document = (_, _) => Task.FromResult(new DocumentStageOutcome(StageState.Failed, StageState.NotStarted, StageState.NotStarted, null, [], [], "OCR_UNAVAILABLE"));

        await rig.RunAsync();

        rig.Request.Status.ShouldBe(VerificationStatus.Failed);
        rig.Request.ErrorCode.ShouldBe("OCR_UNAVAILABLE");
        rig.Request.ExtractionState.ShouldBe(StageState.NotStarted); // nothing was recorded as done
        rig.Meter.Charges.ShouldBe([(VerificationStage.Extraction, MeterOutcome.Failed, rig.Request.Id)]); // the policy decides; the default policy bills nothing for Failed
        rig.Request.CreditsCharged.ShouldBe(0);
        rig.Retention.Erased.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_faulting_module_requeues_with_backoff_without_logging_what_it_said_and_fails_at_the_attempt_cap()
    {
        var rig = Pan();
        rig.Module.Document = (_, _) => throw new InvalidOperationException("boom " + ProcessorRig.CanaryName + " ABCPT1234F");

        await rig.RunAsync();

        rig.Request.Status.ShouldBe(VerificationStatus.Queued);
        rig.Request.Attempts.ShouldBe(1);
        rig.Request.NextAttemptAt!.Value.ShouldBeGreaterThanOrEqualTo(ProcessorRig.Start.UtcDateTime.AddSeconds(30));
        rig.Request.NextAttemptAt!.Value.ShouldBeLessThanOrEqualTo(ProcessorRig.Start.UtcDateTime.AddSeconds(34));
        rig.Request.LeaseToken.ShouldBeNull();
        rig.Retention.Erased.ShouldBeEmpty();
        rig.Logs.All.ShouldNotContain(ProcessorRig.CanaryName);
        rig.Logs.All.ShouldNotContain("ABCPT1234F");
        rig.Logs.All.ShouldContain("InvalidOperationException"); // the type is logged, never the message

        // The same request, claimed again until the cap (3): Failed, data erased.
        for (var attempt = 2; attempt <= 3; attempt++)
        {
            rig.Request.Claim(rig.LeaseToken, ProcessorRig.Start.UtcDateTime.AddMinutes(5), ProcessorRig.Start.UtcDateTime);
            await rig.RunAsync();
        }

        rig.Request.Status.ShouldBe(VerificationStatus.Failed);
        rig.Request.ErrorCode.ShouldBe("PROCESSING_FAILED");
        rig.Retention.Erased.ShouldBe([rig.Request.Id]);
        rig.Meter.Charges.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_module_that_overruns_the_stage_budget_is_cut_off_and_retried()
    {
        var rig = Pan();
        rig.Options.Value.Processing.TimeoutSeconds = 1;
        rig.Module.Document = async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ScriptedModule.DefaultDocument(null);
        };

        await rig.RunAsync();

        rig.Request.Status.ShouldBe(VerificationStatus.Queued);
        rig.Request.Events[^1].NoteCode.ShouldBe("PROCESSING_TIMEOUT");
    }

    [Fact]
    public async Task Shutdown_propagates_so_the_worker_can_hand_the_request_back()
    {
        var rig = Pan();
        using var cts = new CancellationTokenSource();
        rig.Module.Document = async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return ScriptedModule.DefaultDocument(null);
        };

        await Should.ThrowAsync<OperationCanceledException>(() => rig.RunAsync(cts.Token));
        rig.Request.Status.ShouldBe(VerificationStatus.Processing); // untouched: the worker's hand-back gives the lease up without counting the attempt
    }

    [Fact]
    public async Task A_claim_that_lost_its_lease_changes_nothing_and_charges_nothing()
    {
        var rig = Pan();
        rig.Request.ReturnToQueue(ProcessorRig.Start.UtcDateTime, countAttempt: true, ProcessorRig.Start.UtcDateTime, "LEASE_EXPIRED"); // the sweeper took it back
        rig.Request.Claim(Guid.NewGuid(), ProcessorRig.Start.UtcDateTime.AddMinutes(5), ProcessorRig.Start.UtcDateTime); // and another node claimed it

        await rig.Processor.ProcessAsync(new ClaimedRequest(rig.Request.Id, rig.ClientId, VerificationType.Pan, rig.LeaseToken, 1), CancellationToken.None);

        rig.Module.DocumentCalls.ShouldBe(0);
        rig.Meter.Charges.ShouldBeEmpty();
        rig.Request.Status.ShouldBe(VerificationStatus.Processing);
        rig.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cancel_request_ends_the_run_and_keeps_nothing_when_no_stage_was_delivered()
    {
        var rig = Pan();
        rig.Request.RequestCancel(ProcessorRig.Start.UtcDateTime, VerificationActorType.User, null);
        rig.Request.Status.ShouldBe(VerificationStatus.Processing);

        await rig.RunAsync();

        rig.Request.Status.ShouldBe(VerificationStatus.Cancelled);
        rig.Module.DocumentCalls.ShouldBe(0);
        rig.Retention.Erased.ShouldBe([rig.Request.Id]);
        rig.Meter.Charges.ShouldBeEmpty();
        rig.Webhooks.Published.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_concurrent_change_rolls_the_commit_back_and_it_is_retried_on_fresh_state()
    {
        var rig = Pan();
        rig.Uow.ConflictsToThrow = 1;

        await rig.RunAsync();

        rig.Request.Status.ShouldBe(VerificationStatus.Completed);
        rig.Meter.Charges.Count(c => c.Stage == VerificationStage.Extraction).ShouldBe(2); // charged inside the rolled-back transaction, then again idempotently (same ledger key)
    }

    // ---- provider stage and Rule V-1 -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_configured_provider_nothing_is_called_or_charged_and_the_state_is_ProviderNotConfigured()
    {
        var rig = Pan(configured: false);

        await rig.RunAsync();

        rig.Module.ExternalCalls.ShouldBe(0);
        rig.Request.ExternalState.ShouldBe(ExternalState.ProviderNotConfigured);
        rig.Request.ExternalProviderId.ShouldBeNull();
        rig.Request.Status.ShouldBe(VerificationStatus.Completed);
        rig.Request.Assurance.ShouldBe(AssuranceLevel.ChecksCompleted);
        rig.Request.ProviderCreditsCharged.ShouldBe(0);
        rig.Meter.Charges.Select(c => c.Stage).ShouldBe([VerificationStage.Extraction]);
        rig.Results.Checks.ShouldContain(c => c.Code == "EXTERNAL_NOT_PERFORMED" && c.Stage == CheckStage.External);
    }

    [Fact]
    public async Task A_verified_answer_with_a_reference_is_the_only_way_to_authoritative_assurance_and_it_charges_the_provider_stage()
    {
        var rig = Pan(configured: true);

        await rig.RunAsync();

        rig.Request.ExternalState.ShouldBe(ExternalState.Verified);
        rig.Request.ExternalReference.ShouldBe("ref-1");
        rig.Request.Assurance.ShouldBe(AssuranceLevel.AuthoritativelyVerified);
        rig.Request.ProviderCreditsCharged.ShouldBe(2);
        rig.Request.ProviderAttempts.ShouldBe(1);
        rig.Meter.Charges.Select(c => (c.Stage, c.Outcome)).ShouldBe([(VerificationStage.Extraction, MeterOutcome.Success), (VerificationStage.Provider, MeterOutcome.Success)]);
        rig.Module.LastExternalContext!.Extracted.ShouldBeOfType<PanData>(); // the provider stage works from the sealed extraction, not the document
        rig.Module.LastExternalContext.Document.IsEmpty.ShouldBeTrue();
        rig.Results.Rows[rig.Request.Id].FieldMatchesJson.ShouldContain("provider.name");
    }

    [Theory]
    [InlineData(ExternalState.NotVerified, MeterOutcome.NoMatch, VerificationStatus.ReviewRequired)]
    [InlineData(ExternalState.Inconclusive, null, VerificationStatus.ReviewRequired)]
    [InlineData(ExternalState.ProviderError, null, VerificationStatus.ReviewRequired)]
    [InlineData(ExternalState.Timeout, null, VerificationStatus.ReviewRequired)]
    public async Task Only_definitive_provider_answers_are_charged(ExternalState state, MeterOutcome? charged, VerificationStatus status)
    {
        var rig = Pan(configured: true);
        rig.Module.External = (_, _) => Task.FromResult(new ExternalStageOutcome(state, "fake-pan", state == ExternalState.NotVerified ? "ref-n" : null, [], null));

        await rig.RunAsync();

        rig.Request.ExternalState.ShouldBe(state);
        rig.Request.Status.ShouldBe(status);
        rig.Request.Assurance.ShouldNotBe(AssuranceLevel.AuthoritativelyVerified);
        rig.Meter.Charges.Any(c => c.Stage == VerificationStage.Provider).ShouldBe(charged is not null);
    }

    [Fact]
    public async Task A_verified_answer_without_a_reference_is_downgraded_and_never_becomes_verified()
    {
        var rig = Pan(configured: true);
        rig.Module.External = (_, _) => Task.FromResult(new ExternalStageOutcome(ExternalState.Verified, "fake-pan", null, [], null));

        await rig.RunAsync();

        rig.Request.ExternalState.ShouldBe(ExternalState.ProviderError);
        rig.Request.Assurance.ShouldNotBe(AssuranceLevel.AuthoritativelyVerified);
        rig.Meter.Charges.Any(c => c.Stage == VerificationStage.Provider).ShouldBeFalse();
    }

    [Fact]
    public async Task A_provider_stage_that_cannot_be_afforded_is_not_called_and_flags_the_request_for_review()
    {
        var rig = Pan(configured: true);
        rig.Meter.StagePreflightError = Error.PaymentRequired("LICENSE_INSUFFICIENT_BALANCE", "no");

        await rig.RunAsync();

        rig.Module.ExternalCalls.ShouldBe(0); // never call a paid vendor we could not bill
        rig.Request.ExternalState.ShouldBe(ExternalState.NotRequested);
        rig.Request.Status.ShouldBe(VerificationStatus.ReviewRequired);
        rig.Request.ReviewReasonCodes.ShouldContain(ReviewCodes.ProviderStageUnfunded);
        rig.Request.CreditsCharged.ShouldBe(5); // extraction was delivered and stays charged
    }

    [Fact]
    public async Task A_provider_charge_refused_at_commit_discards_the_answer()
    {
        var rig = Pan(configured: true);
        rig.Meter.ChargeErrors[VerificationStage.Provider] = Error.PaymentRequired("LICENSE_INSUFFICIENT_BALANCE", "no");

        await rig.RunAsync();

        rig.Request.ExternalState.ShouldBe(ExternalState.NotRequested);
        rig.Request.ReviewReasonCodes.ShouldContain(ReviewCodes.ProviderStageUnfunded);
        rig.Request.Assurance.ShouldNotBe(AssuranceLevel.AuthoritativelyVerified);
    }

    [Fact]
    public async Task A_provider_fault_that_throws_is_recorded_as_ProviderError_not_as_a_processing_failure()
    {
        var rig = Pan(configured: true);
        rig.Module.External = (_, _) => throw new HttpRequestException("network down with " + ProcessorRig.CanaryName);

        await rig.RunAsync();

        rig.Request.ExternalState.ShouldBe(ExternalState.ProviderError);
        rig.Request.Status.ShouldBe(VerificationStatus.ReviewRequired);
        rig.Logs.All.ShouldNotContain(ProcessorRig.CanaryName);
    }

    [Fact]
    public async Task A_provider_retry_run_skips_the_document_stage_and_runs_only_the_provider_stage()
    {
        var rig = Pan(configured: true);
        rig.Module.External = (_, _) => Task.FromResult(new ExternalStageOutcome(ExternalState.Timeout, "fake-pan", null, [], null));
        await rig.RunAsync();
        rig.Request.Status.ShouldBe(VerificationStatus.ReviewRequired);
        rig.Module.DocumentCalls.ShouldBe(1);
        rig.Meter.Charges.Clear();

        // retry-external: re-queued (not straight to Processing), then claimed under a fresh lease like any request.
        rig.Request.RequeueForExternalRetry(ProcessorRig.Start.UtcDateTime, VerificationActorType.User, Guid.NewGuid());
        rig.Request.Status.ShouldBe(VerificationStatus.Queued);
        rig.Request.Claim(rig.LeaseToken, ProcessorRig.Start.UtcDateTime.AddMinutes(5), ProcessorRig.Start.UtcDateTime);
        rig.Module.External = (_, _) => Task.FromResult(new ExternalStageOutcome(ExternalState.Verified, "fake-pan", "ref-9", [], null));

        await rig.RunAsync();

        rig.Module.DocumentCalls.ShouldBe(1); // not run again
        rig.Request.Status.ShouldBe(VerificationStatus.Completed);
        rig.Request.ExternalState.ShouldBe(ExternalState.Verified);
        rig.Request.ReviewRequired.ShouldBeFalse(); // the provider-derived review reason was cleared by the better answer
        rig.Meter.Charges.Select(c => c.Stage).ShouldBe([VerificationStage.Provider]); // extraction is not charged twice
    }
}
