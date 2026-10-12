using NexaVerify.Domain.Common;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Domain.UnitTests.Verification;

public class VerificationRequestTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Client = Guid.NewGuid();

    private static VerificationRequest NewRequest(VerificationType type = VerificationType.Pan, byte[]? sha = null) =>
        VerificationRequest.Create(Guid.Empty, Client, type, SubmitSource.Api, VerificationActorType.ApiKey, Guid.NewGuid(), "ref-1", "consent-1",
            "idem", sha ?? new byte[32], 1, "corr", "10.0.0.1", Now, Now.AddDays(7));

    private static VerificationRequest Processing(VerificationType type = VerificationType.Pan)
    {
        var request = NewRequest(type);
        request.Claim(Guid.NewGuid(), Now.AddMinutes(5), Now);
        return request;
    }

    private static VerificationRequest InStatus(VerificationStatus status)
    {
        var r = NewRequest();
        switch (status)
        {
            case VerificationStatus.Queued:
                break;
            case VerificationStatus.Processing:
                r.Claim(Guid.NewGuid(), Now.AddMinutes(5), Now);
                break;
            case VerificationStatus.Completed:
                r.Claim(Guid.NewGuid(), Now.AddMinutes(5), Now);
                r.Complete(Now);
                break;
            case VerificationStatus.ReviewRequired:
                r.Claim(Guid.NewGuid(), Now.AddMinutes(5), Now);
                r.AddReviewReason("X");
                r.Complete(Now);
                break;
            case VerificationStatus.Failed:
                r.Claim(Guid.NewGuid(), Now.AddMinutes(5), Now);
                r.Fail("PROCESSING_FAILED", Now);
                break;
            case VerificationStatus.Cancelled:
                r.Cancel(Now);
                break;
            case VerificationStatus.Expired:
                r.Expire(Now);
                break;
        }

        r.Status.ShouldBe(status);
        return r;
    }

    private static readonly (VerificationStatus From, VerificationStatus To)[] Allowed =
    [
        (VerificationStatus.Queued, VerificationStatus.Processing),
        (VerificationStatus.Queued, VerificationStatus.Cancelled),
        (VerificationStatus.Queued, VerificationStatus.Expired),
        (VerificationStatus.Processing, VerificationStatus.Queued),
        (VerificationStatus.Processing, VerificationStatus.Completed),
        (VerificationStatus.Processing, VerificationStatus.ReviewRequired),
        (VerificationStatus.Processing, VerificationStatus.Failed),
        (VerificationStatus.Processing, VerificationStatus.Cancelled),
        (VerificationStatus.Completed, VerificationStatus.Queued),
        (VerificationStatus.ReviewRequired, VerificationStatus.Queued),
    ];

    public static TheoryData<VerificationStatus, VerificationStatus> AllPairs()
    {
        var data = new TheoryData<VerificationStatus, VerificationStatus>();
        foreach (var from in Enum.GetValues<VerificationStatus>())
        {
            foreach (var to in Enum.GetValues<VerificationStatus>())
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Transition_follows_exactly_the_allowed_table(VerificationStatus from, VerificationStatus to)
    {
        var request = InStatus(from);
        var expected = Allowed.Contains((from, to));

        VerificationRules.CanTransition(from, to).ShouldBe(expected);
        if (expected)
        {
            request.Transition(to, Now);
            request.Status.ShouldBe(to);
        }
        else
        {
            Should.Throw<DomainException>(() => request.Transition(to, Now)).Code.ShouldBe(VerificationDomainErrors.InvalidTransition);
            request.Status.ShouldBe(from);
        }
    }

    [Fact]
    public void Create_starts_queued_with_the_upload_recorded_and_a_creation_event()
    {
        var request = NewRequest();

        request.Status.ShouldBe(VerificationStatus.Queued);
        request.UploadState.ShouldBe(StageState.Completed);
        request.ExternalState.ShouldBe(ExternalState.NotRequested);
        request.Assurance.ShouldBe(AssuranceLevel.None);
        request.ReviewRequired.ShouldBeFalse();
        request.Events.Single().FromStatus.ShouldBeNull();
        request.Events.Single().ToStatus.ShouldBe(VerificationStatus.Queued);
        request.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void Number_only_request_has_no_upload()
    {
        var request = VerificationRequest.Create(Guid.Empty, Client, VerificationType.Pan, SubmitSource.Portal, VerificationActorType.User, Guid.NewGuid(),
            null, "c", null, null, null, "corr", null, Now, null);

        request.UploadState.ShouldBe(StageState.Skipped);
    }

    [Fact]
    public void Every_transition_appends_an_event()
    {
        var request = NewRequest();
        request.Claim(Guid.NewGuid(), Now.AddMinutes(5), Now);
        request.Complete(Now);

        request.Events.Select(e => e.ToStatus).ShouldBe([VerificationStatus.Queued, VerificationStatus.Processing, VerificationStatus.Completed]);
        request.Events.All(e => e.ClientId == Client && e.RequestId == request.Id).ShouldBeTrue();
    }

    [Fact]
    public void Claim_counts_the_attempt_and_return_to_queue_can_give_it_back()
    {
        var request = NewRequest();
        request.Claim(Guid.NewGuid(), Now.AddMinutes(5), Now);
        request.Attempts.ShouldBe(1);
        request.LeaseToken.ShouldNotBeNull();

        request.ReturnToQueue(Now.AddSeconds(30), countAttempt: false, Now, "SHUTDOWN");

        request.Status.ShouldBe(VerificationStatus.Queued);
        request.Attempts.ShouldBe(0);
        request.LeaseToken.ShouldBeNull();
        request.NextAttemptAt.ShouldBe(Now.AddSeconds(30));
    }

    // ---- Rule V-1 ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "ref")]
    [InlineData("pan-provider", null)]
    [InlineData("pan-provider", "  ")]
    public void Verified_without_provider_id_and_reference_is_rejected(string? providerId, string? reference)
    {
        var request = Processing();

        var ex = Should.Throw<DomainException>(() => request.RecordExternalResult(ExternalState.Verified, providerId, reference, Now));

        ex.Code.ShouldBe(VerificationDomainErrors.VerifiedRequiresProvider);
        request.ExternalState.ShouldBe(ExternalState.NotRequested);
        request.Assurance.ShouldNotBe(AssuranceLevel.AuthoritativelyVerified);
    }

    [Fact]
    public void Verified_with_provider_id_and_reference_is_authoritative()
    {
        var request = Processing();

        request.RecordExternalResult(ExternalState.Verified, "pan-provider", "ref-123", Now);

        request.ExternalState.ShouldBe(ExternalState.Verified);
        request.Assurance.ShouldBe(AssuranceLevel.AuthoritativelyVerified);
        request.ExternalCompletedAt.ShouldBe(Now);
        request.ReviewRequired.ShouldBeFalse();
    }

    [Theory]
    [InlineData(StageState.Completed, StageState.Completed, StageState.Completed, AssuranceLevel.ChecksCompleted)]
    [InlineData(StageState.Completed, StageState.NotStarted, StageState.NotStarted, AssuranceLevel.ExtractedOnly)]
    [InlineData(StageState.Completed, StageState.Completed, StageState.Failed, AssuranceLevel.ExtractedOnly)]
    [InlineData(StageState.NotStarted, StageState.NotStarted, StageState.NotStarted, AssuranceLevel.None)]
    [InlineData(StageState.Failed, StageState.NotStarted, StageState.NotStarted, AssuranceLevel.None)]
    public void Assurance_without_a_provider_never_exceeds_checks_completed(StageState extracted, StageState rules, StageState consistency, AssuranceLevel expected)
    {
        var request = Processing();
        request.RecordDocumentStage(extracted, rules, consistency, []);

        request.Assurance.ShouldBe(expected);
        request.Assurance.ShouldNotBe(AssuranceLevel.AuthoritativelyVerified);
    }

    [Theory]
    [InlineData(ExternalState.NotRequested)]
    [InlineData(ExternalState.ProviderNotConfigured)]
    [InlineData(ExternalState.Pending)]
    [InlineData(ExternalState.NotVerified)]
    [InlineData(ExternalState.Inconclusive)]
    [InlineData(ExternalState.ProviderError)]
    [InlineData(ExternalState.Timeout)]
    public void Only_a_provider_verified_answer_yields_authoritative_assurance(ExternalState state)
    {
        VerificationRules.ComputeAssurance(StageState.Completed, StageState.Completed, StageState.Completed, state, "p", "r")
            .ShouldBe(AssuranceLevel.ChecksCompleted);
        VerificationRules.ComputeAssurance(StageState.Completed, StageState.Completed, StageState.Completed, ExternalState.Verified, "p", "r")
            .ShouldBe(AssuranceLevel.AuthoritativelyVerified);
        VerificationRules.ComputeAssurance(StageState.Completed, StageState.Completed, StageState.Completed, ExternalState.Verified, "p", null)
            .ShouldBe(AssuranceLevel.ChecksCompleted);
    }

    [Fact]
    public void Definitive_provider_answers_cannot_be_replaced()
    {
        var request = Processing();
        request.RecordExternalResult(ExternalState.NotVerified, "p", "r", Now);

        Should.Throw<DomainException>(() => request.RecordExternalResult(ExternalState.Verified, "p", "r2", Now))
            .Code.ShouldBe(VerificationDomainErrors.ExternalResultFinal);
        Should.Throw<DomainException>(() => request.MarkExternalPending()).Code.ShouldBe(VerificationDomainErrors.ExternalResultFinal);
    }

    [Theory]
    [InlineData(ExternalState.NotVerified, ReviewCodes.ProviderNotVerified)]
    [InlineData(ExternalState.Inconclusive, ReviewCodes.ProviderInconclusive)]
    [InlineData(ExternalState.ProviderError, ReviewCodes.ProviderUnavailable)]
    [InlineData(ExternalState.Timeout, ReviewCodes.ProviderUnavailable)]
    public void Negative_or_failed_provider_answers_require_review(ExternalState state, string code)
    {
        var request = Processing();

        request.RecordExternalResult(state, "p", null, Now);
        request.Complete(Now);

        request.ReviewRequired.ShouldBeTrue();
        request.ReviewReasonCodes.ShouldContain(code);
        request.Status.ShouldBe(VerificationStatus.ReviewRequired);
    }

    [Fact]
    public void Provider_not_configured_alone_does_not_require_review()
    {
        var request = Processing();

        request.RecordExternalResult(ExternalState.ProviderNotConfigured, null, null, Now);
        request.Complete(Now);

        request.ReviewRequired.ShouldBeFalse();
        request.Status.ShouldBe(VerificationStatus.Completed);
        request.Assurance.ShouldNotBe(AssuranceLevel.AuthoritativelyVerified);
    }

    [Fact]
    public void External_results_can_only_be_recorded_while_processing()
    {
        var request = NewRequest();

        Should.Throw<DomainException>(() => request.RecordExternalResult(ExternalState.ProviderNotConfigured, null, null, Now))
            .Code.ShouldBe(VerificationDomainErrors.InvalidState);
    }

    [Fact]
    public void Retry_external_reopens_a_finished_request_and_a_later_verified_clears_provider_review_reasons()
    {
        var request = Processing();
        request.RecordDocumentStage(StageState.Completed, StageState.Completed, StageState.Completed, []);
        request.RecordExternalResult(ExternalState.ProviderError, "p", null, Now);
        request.Complete(Now);
        request.Status.ShouldBe(VerificationStatus.ReviewRequired);

        request.RequeueForExternalRetry(Now, VerificationActorType.User, Guid.NewGuid());
        request.Status.ShouldBe(VerificationStatus.Queued);
        request.Attempts.ShouldBe(0);
        request.LeaseToken.ShouldBeNull();
        request.CompletedAt.ShouldBeNull();
        request.NextAttemptAt.ShouldBe(Now);
        request.Events[^1].NoteCode.ShouldBe("EXTERNAL_RETRY");
        request.Claim(Guid.NewGuid(), Now.AddMinutes(5), Now); // a retry runs under a lease like any other claim
        request.Status.ShouldBe(VerificationStatus.Processing);
        request.LeaseToken.ShouldNotBeNull();
        request.RecordExternalResult(ExternalState.Verified, "p", "ref", Now);
        request.Complete(Now);

        request.Status.ShouldBe(VerificationStatus.Completed);
        request.ReviewRequired.ShouldBeFalse();
        request.Assurance.ShouldBe(AssuranceLevel.AuthoritativelyVerified);
    }

    [Fact]
    public void Retry_external_is_refused_for_definitive_answers_and_purged_data()
    {
        var verified = Processing();
        verified.RecordExternalResult(ExternalState.Verified, "p", "r", Now);
        verified.Complete(Now);
        Should.Throw<DomainException>(() => verified.RequeueForExternalRetry(Now, VerificationActorType.User, null))
            .Code.ShouldBe(VerificationDomainErrors.NotRetryable);

        var purged = Processing();
        purged.RecordExternalResult(ExternalState.Timeout, "p", null, Now);
        purged.Complete(Now);
        purged.MarkPurged(Now);
        Should.Throw<DomainException>(() => purged.RequeueForExternalRetry(Now, VerificationActorType.User, null))
            .Code.ShouldBe(VerificationDomainErrors.NotRetryable);
    }

    [Fact]
    public void Document_stage_flags_only_advance_from_not_started()
    {
        var request = Processing();
        request.RecordDocumentStage(StageState.Completed, StageState.Completed, StageState.Completed, ["BS_COMPLETENESS"]);

        request.RecordDocumentStage(StageState.Failed, StageState.Failed, StageState.Failed, []);

        request.ExtractionState.ShouldBe(StageState.Completed);
        request.RulesState.ShouldBe(StageState.Completed);
        request.ConsistencyState.ShouldBe(StageState.Completed);
        request.ReviewReasonCodes.ShouldBe(["BS_COMPLETENESS"]);
        request.ReviewRequired.ShouldBeTrue();
    }

    [Fact]
    public void Cancel_of_queued_is_immediate_and_of_processing_only_sets_the_flag()
    {
        var queued = NewRequest();
        queued.RequestCancel(Now, VerificationActorType.User, Guid.NewGuid());
        queued.Status.ShouldBe(VerificationStatus.Cancelled);
        queued.ErrorCode.ShouldBe("CANCELLED");

        var processing = Processing();
        processing.RequestCancel(Now, VerificationActorType.User, Guid.NewGuid());
        processing.Status.ShouldBe(VerificationStatus.Processing);
        processing.CancelRequestedAt.ShouldBe(Now);
        processing.Cancel(Now);
        processing.Status.ShouldBe(VerificationStatus.Cancelled);

        var done = InStatus(VerificationStatus.Completed);
        Should.Throw<DomainException>(() => done.RequestCancel(Now, VerificationActorType.User, null)).Code.ShouldBe(VerificationDomainErrors.NotCancellable);
    }

    [Fact]
    public void Fail_and_expire_record_the_code_and_terminate()
    {
        var failed = Processing();
        failed.Fail("DOCUMENT_UNREADABLE", Now);
        failed.IsTerminal.ShouldBeTrue();
        failed.ErrorCode.ShouldBe("DOCUMENT_UNREADABLE");
        failed.LeaseToken.ShouldBeNull();

        var expired = NewRequest();
        expired.Expire(Now);
        expired.Status.ShouldBe(VerificationStatus.Expired);
        expired.ErrorCode.ShouldBe("EXPIRED");
    }

    [Fact]
    public void Review_reason_codes_round_trip_as_json_codes_only()
    {
        var request = Processing();
        request.AddReviewReason("A");
        request.AddReviewReason("A");
        request.AddReviewReason("B");

        request.ReviewReasonCodesJson.ShouldBe("[\"A\",\"B\"]");
    }

    // ---- Billing mapping ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_verification_operation_is_priced_by_default_and_maps_to_exactly_one_type_and_stage()
    {
        var verificationOps = Enum.GetValues<MeteredOperation>().Where(VerificationOperations.IsVerificationOperation).ToList();
        verificationOps.Count.ShouldBe(5);

        var mapped = new List<MeteredOperation>();
        foreach (var type in Enum.GetValues<VerificationType>())
        {
            foreach (var stage in Enum.GetValues<VerificationStage>())
            {
                if (VerificationOperations.For(type, stage) is { } op)
                {
                    mapped.Add(op);
                }
            }
        }

        mapped.Order().ShouldBe(verificationOps.Order());
        foreach (var op in verificationOps)
        {
            VerificationOperations.DefaultCredits.ShouldContainKey(op);
            VerificationOperations.DefaultCredits[op].ShouldBeGreaterThan(0);
        }

        VerificationOperations.For(VerificationType.BankStatement, VerificationStage.Provider).ShouldBeNull();
        VerificationOperations.IsVerificationOperation(MeteredOperation.Detect).ShouldBeFalse();
    }

    [Fact]
    public void Default_prices_match_the_design()
    {
        VerificationOperations.DefaultCredits[MeteredOperation.BankStatementExtraction].ShouldBe(5);
        VerificationOperations.DefaultCredits[MeteredOperation.PanExtraction].ShouldBe(1);
        VerificationOperations.DefaultCredits[MeteredOperation.PanProviderCheck].ShouldBe(2);
        VerificationOperations.DefaultCredits[MeteredOperation.PassportExtraction].ShouldBe(2);
        VerificationOperations.DefaultCredits[MeteredOperation.PassportProviderCheck].ShouldBe(3);
        VerificationOperations.DefaultPolicy.ShouldBe(ChargePolicy.OnCompleted);
    }

    // ---- Value object / grants ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ABCDE1234F", "XXXXXXX34F")]
    [InlineData("abcde-1234-f", "XXXXXXX34F")]
    [InlineData("1234", "XXX4")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Masked_identifier_keeps_only_the_tail(string? input, string expected)
    {
        MaskedIdentifier.From(input, 4).Value.ShouldBe(expected);
    }

    [Fact]
    public void Masked_identifier_is_length_capped_and_hides_the_head()
    {
        MaskedIdentifier.From(new string('9', 100), 4).Value.Length.ShouldBeLessThanOrEqualTo(MaskedIdentifier.MaxLength);
        MaskedIdentifier.From("ABCDE1234F", 4).Value.ShouldNotContain("ABCDE");
    }

    [Fact]
    public void Download_grant_is_usable_only_before_expiry_and_before_use()
    {
        var grant = VerificationDownloadGrant.Create(Client, Guid.NewGuid(), DownloadKind.Report, "json", Guid.NewGuid(), new byte[32], Now, TimeSpan.FromSeconds(60));

        grant.IsUsable(Now.AddSeconds(59)).ShouldBeTrue();
        grant.IsUsable(Now.AddSeconds(60)).ShouldBeFalse();
    }

    [Fact]
    public void The_retention_date_is_the_latest_purge_time_and_can_be_moved()
    {
        var request = NewRequest();
        request.ExpiresAt.ShouldBe(Now.AddDays(7));
        request.SetExpiresAt(Now.AddDays(90));
        request.ExpiresAt.ShouldBe(Now.AddDays(90));
    }

    [Fact]
    public void Retry_external_from_a_finished_request_goes_through_the_queue_never_straight_to_processing()
    {
        var request = Processing();
        request.RecordDocumentStage(StageState.Completed, StageState.Completed, StageState.Completed, []);
        request.RecordExternalResult(ExternalState.ProviderNotConfigured, null, null, Now);
        request.Complete(Now);

        request.RequeueForExternalRetry(Now.AddHours(1), VerificationActorType.ApiKey, null);

        request.Status.ShouldBe(VerificationStatus.Queued);
        request.LeaseToken.ShouldBeNull();
        request.ExtractionState.ShouldBe(StageState.Completed); // the document stage is not repeated
        request.ExternalState.ShouldBe(ExternalState.ProviderNotConfigured);
        VerificationRules.CanTransition(VerificationStatus.Completed, VerificationStatus.Processing).ShouldBeFalse();
        VerificationRules.CanTransition(VerificationStatus.ReviewRequired, VerificationStatus.Processing).ShouldBeFalse();
        Should.Throw<DomainException>(() => request.RequeueForExternalRetry(Now, VerificationActorType.User, null)).Code.ShouldBe(VerificationDomainErrors.NotRetryable); // already queued
    }

    [Fact]
    public void Field_outcomes_of_a_result_can_be_extended_without_touching_the_sealed_data()
    {
        var result = VerificationResult.Create(Client, Guid.NewGuid(), 1, [1, 2, 3], null, null, "[]", Now.AddDays(1));
        result.SetFieldMatches("[{\"field\":\"name\",\"outcome\":\"Match\"}]");
        result.FieldMatchesJson.ShouldContain("name");
        result.DataEnc.ShouldBe([1, 2, 3]);
    }
}
