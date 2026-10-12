using System.Text;
using System.Text.Json;
using NexaVerify.Application.Common;
using NexaVerify.Application.Licensing;
using NexaVerify.Application.UnitTests.Verification.Processing;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Billing;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Application.Verification.Query;
using NexaVerify.Application.Verification.Submission;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.UnitTests.Verification;

public class VerificationMeterTests
{
    private sealed class FakeMetering : ILicenseMeteringService
    {
        public List<ChargeCommand> Charges { get; } = [];

        public Dictionary<MeteredOperation, MeterTicket> Tickets { get; } = [];

        public Error? ChargeError { get; set; }

        public Task<Result<MeterTicket>> PreflightAsync(Guid clientId, MeteredOperation operation, CancellationToken cancellationToken) =>
            Task.FromResult(Tickets.TryGetValue(operation, out var t) ? Result<MeterTicket>.Success(t) : new MeterTicket(operation, 1, ChargePolicy.OnCompleted, 100));

        public Task<Result<ChargeResult>> ChargeAsync(ChargeCommand command, CancellationToken cancellationToken)
        {
            Charges.Add(command);
            return Task.FromResult(ChargeError is null ? Result<ChargeResult>.Success(new ChargeResult(3, 97, Guid.NewGuid(), 42, false)) : ChargeError);
        }
    }

    [Theory]
    [InlineData(VerificationType.BankStatement, VerificationStage.Extraction, MeteredOperation.BankStatementExtraction)]
    [InlineData(VerificationType.Pan, VerificationStage.Extraction, MeteredOperation.PanExtraction)]
    [InlineData(VerificationType.Pan, VerificationStage.Provider, MeteredOperation.PanProviderCheck)]
    [InlineData(VerificationType.Passport, VerificationStage.Extraction, MeteredOperation.PassportExtraction)]
    [InlineData(VerificationType.Passport, VerificationStage.Provider, MeteredOperation.PassportProviderCheck)]
    public async Task Each_stage_is_charged_through_the_ledger_under_its_own_operation_and_stable_key(VerificationType type, VerificationStage stage, MeteredOperation operation)
    {
        var metering = new FakeMetering();
        var requestId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        var charge = await new VerificationMeter(metering).ChargeStageAsync(clientId, requestId, type, stage, MeterOutcome.Success, CancellationToken.None);

        charge.IsSuccess.ShouldBeTrue();
        charge.Value.ShouldBe(new StageCharge(3, 97, 42, false));
        var command = metering.Charges.ShouldHaveSingleItem();
        command.ClientId.ShouldBe(clientId);
        command.Operation.ShouldBe(operation);
        command.RecognitionRequestId.ShouldBe(requestId); // the ledger row is traceable to the request
        command.IdempotencyKey.ShouldBe($"vr:{requestId:N}:{(stage == VerificationStage.Extraction ? 'x' : 'p')}");
    }

    [Fact]
    public async Task A_stage_that_does_not_exist_cannot_be_charged_so_no_upcoming_or_unsupported_stage_can_bill()
    {
        var metering = new FakeMetering();
        var meter = new VerificationMeter(metering);

        var bank = await meter.ChargeStageAsync(Guid.NewGuid(), Guid.NewGuid(), VerificationType.BankStatement, VerificationStage.Provider, MeterOutcome.Success, CancellationToken.None);
        bank.IsFailure.ShouldBeTrue();
        (await meter.PreflightStageAsync(Guid.NewGuid(), VerificationType.BankStatement, VerificationStage.Provider, CancellationToken.None)).IsFailure.ShouldBeTrue();
        metering.Charges.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_charge_is_passed_on_unchanged()
    {
        var metering = new FakeMetering { ChargeError = Error.PaymentRequired("LICENSE_INSUFFICIENT_BALANCE", "no") };
        var charge = await new VerificationMeter(metering).ChargeStageAsync(Guid.NewGuid(), Guid.NewGuid(), VerificationType.Pan, VerificationStage.Extraction, MeterOutcome.Success, CancellationToken.None);
        charge.Error!.Code.ShouldBe("LICENSE_INSUFFICIENT_BALANCE");
    }

    [Fact]
    public async Task The_accept_time_pre_flight_requires_credits_for_both_stages_when_a_provider_will_run()
    {
        var metering = new FakeMetering();
        metering.Tickets[MeteredOperation.PanExtraction] = new MeterTicket(MeteredOperation.PanExtraction, 1, ChargePolicy.OnCompleted, 2);
        metering.Tickets[MeteredOperation.PanProviderCheck] = new MeterTicket(MeteredOperation.PanProviderCheck, 2, ChargePolicy.OnCompleted, 2);
        var meter = new VerificationMeter(metering);

        (await meter.PreflightAsync(Guid.NewGuid(), VerificationType.Pan, includeProvider: false, CancellationToken.None)).IsSuccess.ShouldBeTrue();
        var both = await meter.PreflightAsync(Guid.NewGuid(), VerificationType.Pan, includeProvider: true, CancellationToken.None);
        both.Error!.Code.ShouldBe(ErrorCodes.LicenseInsufficientBalance); // 1 + 2 > 2 available

        metering.Tickets[MeteredOperation.PanExtraction] = new MeterTicket(MeteredOperation.PanExtraction, 1, ChargePolicy.OnCompleted, 3);
        metering.Tickets[MeteredOperation.PanProviderCheck] = new MeterTicket(MeteredOperation.PanProviderCheck, 2, ChargePolicy.OnCompleted, 3);
        var enough = await meter.PreflightAsync(Guid.NewGuid(), VerificationType.Pan, includeProvider: true, CancellationToken.None);
        enough.Value.ShouldBe(new VerificationMeterTicket(1, 2, 3));
    }
}

public class VerificationEnvelopeCodecTests
{
    private readonly VerificationEnvelopeCodec _codec = new(new FakeEncryption());

    [Fact]
    public async Task Every_extracted_shape_survives_the_round_trip_with_its_submitted_fields()
    {
        var client = Guid.NewGuid();
        var id = Guid.NewGuid();
        ExtractedData[] shapes =
        [
            new PanData { PanNumber = "ABCPT1234F", NameOnCard = "A B", DateOfBirth = new DateOnly(1990, 8, 15), NumberOcrCorrected = true },
            new PassportData { PassportNumber = "X1234567", Surname = "S", MrzPresent = true, MrzFormat = "TD3", MrzChecks = new(MrzCheckState.Valid, MrzCheckState.Invalid, MrzCheckState.Valid, MrzCheckState.NotPresent, MrzCheckState.Valid) },
            new BankStatementData
            {
                AccountNumberHash = [1, 2, 3], TextSource = TextSource.Mixed, Transactions = [new BankTransaction(1, new DateOnly(2024, 4, 2), null, "UPI", null, 500m, null, 9500m, 0.9m)],
            },
        ];

        foreach (var shape in shapes)
        {
            var sealedData = await _codec.SealAsync(client, id, new SubmissionEnvelope(new Dictionary<string, string?> { ["fullName"] = "X" }, shape), CancellationToken.None);
            var opened = await _codec.OpenAsync(client, id, sealedData, CancellationToken.None);
            opened!.Submitted["fullName"].ShouldBe("X");
            opened.Extracted!.GetType().ShouldBe(shape.GetType());
            if (shape is BankStatementData bank)
            {
                var back = opened.Extracted.ShouldBeOfType<BankStatementData>();
                back.AccountNumberHash.ShouldBe(bank.AccountNumberHash);
                back.TextSource.ShouldBe(TextSource.Mixed);
                back.Transactions.Single().ShouldBe(bank.Transactions.Single());
            }
            else
            {
                opened.Extracted.ShouldBe(shape);
            }
        }
    }

    [Fact]
    public async Task The_sealed_data_cannot_be_opened_for_another_request_or_another_client_or_when_damaged()
    {
        var client = Guid.NewGuid();
        var id = Guid.NewGuid();
        var data = await _codec.SealAsync(client, id, new SubmissionEnvelope(new Dictionary<string, string?>(), new PanData { PanNumber = "ABCPT1234F" }), CancellationToken.None);

        (await _codec.OpenAsync(client, Guid.NewGuid(), data, CancellationToken.None)).ShouldBeNull();
        (await _codec.OpenAsync(Guid.NewGuid(), id, data, CancellationToken.None)).ShouldBeNull();
        (await _codec.OpenAsync(client, id, data[..^2], CancellationToken.None)).ShouldBeNull(); // truncated JSON
        Encoding.UTF8.GetString(data).ShouldNotContain("ABCPT1234F");
    }

    [Fact]
    public void A_record_never_prints_its_contents()
    {
        new SubmissionEnvelope(new Dictionary<string, string?> { ["fullName"] = "CANARY" }, new PanData { PanNumber = "ABCPT1234F" }).ToString().ShouldBe("SubmissionEnvelope");
        new PanData { PanNumber = "ABCPT1234F" }.ToString().ShouldNotContain("ABCPT1234F");
    }
}

public class VerificationReportTests
{
    private static VerificationResultDto Result(string checkMessage = "ok", string status = "Completed", string assurance = "ChecksCompleted") => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"), "Pan", status,
        new VerificationStagesDto("Completed", "Completed", "Completed", "Completed", "ProviderNotConfigured", null), false, assurance, null, null,
        DateTime.UtcNow, "corr", null, null, new VerificationSummaryDto(1, 0, 0, 0),
        [new VerificationCheckDto("PAN_FORMAT", "Rules", "Pass", "Info", "panNumber", checkMessage)], [], [new VerificationFieldMatchDto("fullName", "Match")],
        new VerificationExternalDto("ProviderNotConfigured", null, null, null, "Official verification is not available for this account."),
        new VerificationExtractedDto(null, new PanExtractedDto("XXXXXXX234F", "P", "Individual", "A B", null, null), null), null, new VerificationProcessingDto(null, null, 1));

    [Fact]
    public void The_html_report_encodes_every_value_and_carries_the_fixed_disclaimer()
    {
        var html = Encoding.UTF8.GetString(VerificationReports.Html(Result("<script>alert('x')</script> & \"q\""), DateTime.UtcNow).Content);

        html.ShouldNotContain("<script>");
        html.ShouldContain("&lt;script&gt;");
        html.ShouldContain("&amp;");
        html.ShouldContain(VerificationDtoMapper.Disclaimer.Replace("'", "&#39;"));
        html.ShouldContain("XXXXXXX234F");
    }

    [Fact]
    public void An_unverified_report_never_claims_authenticity_outside_the_disclaimer()
    {
        var json = Encoding.UTF8.GetString(VerificationReports.Json(Result(), DateTime.UtcNow).Content).Replace(VerificationDtoMapper.Disclaimer, string.Empty);
        var html = Encoding.UTF8.GetString(VerificationReports.Html(Result(), DateTime.UtcNow).Content).Replace(System.Net.WebUtility.HtmlEncode(VerificationDtoMapper.Disclaimer), string.Empty);

        foreach (var word in new[] { "authentic", "genuine", "forged", "fake" })
        {
            json.ShouldNotContain(word, Case.Insensitive);
            html.ShouldNotContain(word, Case.Insensitive);
        }

        json.ShouldContain("\"assurance\": \"ChecksCompleted\"");
    }

    [Fact]
    public void The_transactions_csv_neutralises_formulas_and_starts_with_a_byte_order_mark()
    {
        var file = VerificationReports.TransactionsCsv(
            Guid.NewGuid(),
            [new BankTransactionDto(1, new DateOnly(2024, 4, 2), null, "=HYPERLINK(\"http://x\")", "@ref", 500.5m, null, -12m), new BankTransactionDto(2, new DateOnly(2024, 4, 3), null, "plain, with comma", null, null, 10m, 20m)]);

        file.ContentType.ShouldBe("text/csv; charset=utf-8");
        file.Content.Take(3).ShouldBe([0xEF, 0xBB, 0xBF]);
        var text = Encoding.UTF8.GetString(file.Content, 3, file.Content.Length - 3);
        text.ShouldContain("'=HYPERLINK");
        text.ShouldContain("'@ref");
        text.ShouldContain("\"plain, with comma\"");
        text.ShouldContain("500.50");
        text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(3);
    }
}

public class VerificationResultSupportTests
{
    [Fact]
    public void Field_outcomes_are_read_from_consistency_checks_and_never_carry_values()
    {
        CheckResult Check(string code, CheckStage stage, string? field, string message) => new(code, stage, CheckOutcome.Pass, CheckSeverity.Info, field, message);

        var matches = ResultSupport.FieldMatchesFromChecks(
        [
            Check("PAN_NAME_MATCH", CheckStage.Consistency, "fullName", "Mismatch: submitted and extracted value of this field compared."),
            Check("PAN_DOB_MATCH", CheckStage.Consistency, "dateOfBirth", "NotProvided: submitted and extracted value of this field compared."),
            Check("BS_SUBMITTED_MATCH", CheckStage.Consistency, "submitted", "Match: x"), // the aggregate is not a field
            Check("PAN_FORMAT", CheckStage.Rules, "panNumber", "Match: x"), // not a comparison
            Check("PAN_NAME_INITIAL", CheckStage.Consistency, "fullName", "The 5th character agrees"),
        ]);

        matches.Select(m => (m.Field, m.Outcome)).ShouldBe([("fullName", FieldMatchOutcome.Mismatch), ("dateOfBirth", FieldMatchOutcome.NotProvided)]);
        ResultSupport.FromJson(ResultSupport.ToJson(matches)).ShouldBe(matches);
        ResultSupport.FromJson("not json").ShouldBeEmpty();
        ResultSupport.FromJson(null).ShouldBeEmpty();
    }

    [Fact]
    public void Identifiers_are_masked_and_hashed_with_the_platform_key_and_normalised_first()
    {
        var crypto = new FakeCrypto();
        var (masked, hash) = ResultSupport.Identifier(new PanData { PanNumber = "ABCPT1234F" }, crypto);
        masked!.Value.Value.ShouldNotContain("ABCPT");
        masked.Value.Value.ShouldEndWith("34F");
        hash!.Length.ShouldBe(32);

        var (_, spaced) = ResultSupport.Identifier(new PanData { PanNumber = "abcpt 1234-f" }, crypto);
        spaced.ShouldBe(hash); // dedupe works across formatting

        ResultSupport.Identifier(new PanData(), crypto).ShouldBe((null, null));
        ResultSupport.Identifier(null, crypto).ShouldBe((null, null));
    }

    [Theory]
    [InlineData(ExternalState.Verified, CheckOutcome.Pass)]
    [InlineData(ExternalState.NotVerified, CheckOutcome.Fail)]
    [InlineData(ExternalState.Inconclusive, CheckOutcome.Warn)]
    [InlineData(ExternalState.ProviderError, CheckOutcome.Warn)]
    [InlineData(ExternalState.Timeout, CheckOutcome.Warn)]
    [InlineData(ExternalState.ProviderNotConfigured, CheckOutcome.NotApplicable)]
    public void The_external_check_is_positive_only_for_a_verified_answer(ExternalState state, CheckOutcome outcome)
    {
        var check = ResultSupport.ExternalCheck(state);
        check.Outcome.ShouldBe(outcome);
        check.Stage.ShouldBe(CheckStage.External);
        check.Message.ShouldNotContain("genuine", Case.Insensitive);
        check.Message.ShouldNotContain("authentic", Case.Insensitive);
        (check.Outcome == CheckOutcome.Pass).ShouldBe(state == ExternalState.Verified);
        ResultSupport.ExternalCheck(ExternalState.Verified, unfunded: true).Outcome.ShouldBe(CheckOutcome.NotEvaluated);
    }
}

public class VerificationDtoMapperTests
{
    [Fact]
    public void Failure_codes_have_plain_messages_and_unknown_ones_a_safe_default()
    {
        VerificationDtoMapper.ErrorMessage(null).ShouldBeNull();
        foreach (var code in new[] { "DOCUMENT_UNREADABLE", "OCR_UNAVAILABLE", "PROCESSING_FAILED", "PROCESSING_TIMEOUT", "CREDITS_UNAVAILABLE", "CANCELLED", "EXPIRED" })
        {
            VerificationDtoMapper.ErrorMessage(code).ShouldNotBeNullOrWhiteSpace();
        }

        VerificationDtoMapper.ErrorMessage("SOMETHING_NEW").ShouldBe("The verification did not complete.");
    }

    [Fact]
    public void The_extracted_view_masks_identifiers_and_can_hide_personal_fields()
    {
        var options = new VerificationOptions();
        var today = new DateOnly(2026, 10, 12);
        var passport = new PassportData { PassportNumber = "X1234567", Surname = "TESTPERSON", GivenNames = "ASHA", DateOfExpiry = new DateOnly(2020, 1, 1), MrzPresent = true, MrzFormat = "TD3" };

        var shown = VerificationDtoMapper.Extracted(passport, options, today)!.Passport!;
        shown.PassportNumberMasked.ShouldNotContain("X1234567");
        shown.Surname.ShouldBe("TESTPERSON");
        shown.IsExpired.ShouldBeTrue();

        options.Result.IncludeExtractedPersonalFields = false;
        var hidden = VerificationDtoMapper.Extracted(passport, options, today)!.Passport!;
        (hidden.Surname, hidden.GivenNames, hidden.DateOfBirth).ShouldBe((null, null, null));

        var pan = VerificationDtoMapper.Extracted(new PanData { PanNumber = "ABCPT1234F", HolderTypeCode = "P", NameOnCard = "A" }, options, today)!.Pan!;
        pan.PanMasked.ShouldNotContain("ABCPT");
        pan.HolderType.ShouldBe("Individual (Person)");
        pan.NameOnCard.ShouldBeNull();

        var bank = new BankStatementData { Transactions = Enumerable.Range(1, 5).Select(i => new BankTransaction(i, new DateOnly(2024, 4, i), null, "d", null, 1m, null, null, null)).ToList() };
        options.Result.InlineTransactions = 2;
        var view = VerificationDtoMapper.Extracted(bank, options, today)!.BankStatement!;
        view.Transactions.Count.ShouldBe(2);
        view.TransactionsTruncated.ShouldBeTrue();
        view.TransactionCount.ShouldBe(5);
        view.TotalDebits.ShouldBe(5m);

        VerificationDtoMapper.Extracted(null, options, today).ShouldBeNull();
    }
}

public class VerificationOptionsWorkerTests
{
    [Fact]
    public void The_new_worker_limits_must_be_positive()
    {
        var options = new VerificationOptions { Worker = { PerClientLimit = 0, HousekeepingBatch = 0 } };
        var result = new VerificationOptionsValidator().Validate(null, options);
        result.Failed.ShouldBeTrue();
        string.Join('\n', result.Failures!).ShouldContain("Worker:PerClientLimit");
        string.Join('\n', result.Failures!).ShouldContain("Worker:HousekeepingBatch");
        new VerificationOptionsValidator().Validate(null, new VerificationOptions()).Succeeded.ShouldBeTrue();
    }
}
