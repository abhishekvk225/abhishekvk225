using Microsoft.Extensions.Logging;
using NexaVerify.Application.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.BankStatements;
using NexaVerify.Application.Verification.Matching;
using NexaVerify.Application.Verification.Pan;
using NexaVerify.Application.Verification.Parsing;
using NexaVerify.Application.Verification.Passport;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Verification;
using NexaVerify.Infrastructure.Verification.Providers;
using NexaVerify.TestSupport.Verification;

namespace NexaVerify.Infrastructure.UnitTests.Verification;

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal static class Ctx
{
    public static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    public static IOptions<VerificationOptions> Opts { get; } = Options.Create(new VerificationOptions());

    public static ModuleContext Of(VerificationType type, byte[]? doc = null, DocumentKind kind = DocumentKind.Pdf, Dictionary<string, string?>? fields = null, ExtractedData? extracted = null) =>
        new(new VerificationRequestSnapshot(Guid.NewGuid(), Guid.NewGuid(), type, null, "corr-1", 1, StageState.NotStarted, StageState.NotStarted, StageState.NotStarted, ExternalState.NotRequested),
            kind, doc ?? new byte[] { 1 }, fields ?? [], extracted, "corr-1");

    public static CheckResult C(DocumentStageOutcome o, string code) => o.Checks.Single(c => c.Code == code);
}

public class BankStatementProcessorTests
{
    private static BankStatementModuleProcessor Make(FakeDocumentParser parser, ILogger<BankStatementModuleProcessor>? log = null) =>
        new(parser, new BankRuleSetProvider(Ctx.Opts), new BankStatementParser(Ctx.Opts, new FixedClock(Ctx.Now)), new NameMatcher(Ctx.Opts), Ctx.Opts,
            log ?? NullLogger<BankStatementModuleProcessor>.Instance);

    [Fact]
    public async Task Successful_statement_completes_all_document_stages_and_never_claims_verification()
    {
        var o = await Make(FakeDocumentParser.WithLines(SampleTexts.GenericStatement())).ProcessDocumentAsync(Ctx.Of(VerificationType.BankStatement), CancellationToken.None);
        o.Extracted.ShouldBe(StageState.Completed);
        o.Rules.ShouldBe(StageState.Completed);
        o.Consistency.ShouldBe(StageState.Completed);
        o.FailureCode.ShouldBeNull();
        o.Reasons.ShouldBeEmpty();
        o.Data.ShouldBeOfType<BankStatementData>().Transactions.Count.ShouldBe(4);
        Ctx.C(o, BankCheckCodes.ExternalNotPerformed).Stage.ShouldBe(CheckStage.External);
        o.Checks.Select(c => c.Code).Distinct().Count().ShouldBe(o.Checks.Count); // codes are unique
        foreach (var c in o.Checks)
        {
            c.Message.ShouldNotContain("authentic", Case.Insensitive);
            c.Message.ShouldNotContain("genuine", Case.Insensitive);
        }
    }

    [Fact]
    public async Task External_stage_is_not_requested_for_bank_statements()
    {
        var r = await Make(new FakeDocumentParser()).VerifyExternalAsync(Ctx.Of(VerificationType.BankStatement), CancellationToken.None);
        r.State.ShouldBe(ExternalState.NotRequested);
    }

    [Fact]
    public async Task Inconsistent_statement_produces_review_reasons_without_values()
    {
        var lines = SampleTexts.GenericStatement().Select(l => l.Replace("32,500.00", "32,900.00")).ToArray();
        var o = await Make(FakeDocumentParser.WithLines(lines)).ProcessDocumentAsync(Ctx.Of(VerificationType.BankStatement), CancellationToken.None);
        o.Reasons.Select(r => r.Code).ShouldContain(BankCheckCodes.RowArithmetic);
        o.Reasons.ShouldAllBe(r => !r.Message.Contains("32,900", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreadable_and_ocr_unavailable_documents_fail_extraction_only()
    {
        var o = await Make(new FakeDocumentParser()).ProcessDocumentAsync(Ctx.Of(VerificationType.BankStatement), CancellationToken.None);
        o.Extracted.ShouldBe(StageState.Failed);
        o.Rules.ShouldBe(StageState.NotStarted);
        o.Consistency.ShouldBe(StageState.NotStarted);
        o.FailureCode.ShouldBe("DOCUMENT_UNREADABLE");

        var ocr = new FakeDocumentParser { Result = new Error(ErrorCodes.OcrUnavailable, "x", ErrorType.Unavailable) };
        (await Make(ocr).ProcessDocumentAsync(Ctx.Of(VerificationType.BankStatement), CancellationToken.None)).FailureCode.ShouldBe("OCR_UNAVAILABLE");

        var nothing = FakeDocumentParser.WithLines(["lorem ipsum", "dolor sit amet"]);
        (await Make(nothing).ProcessDocumentAsync(Ctx.Of(VerificationType.BankStatement), CancellationToken.None)).FailureCode.ShouldBe("DOCUMENT_UNREADABLE");
    }

    [Fact]
    public async Task Submitted_fields_are_compared_per_field()
    {
        var fields = new Dictionary<string, string?>
        {
            [SubmittedFieldKeys.AccountHolderName] = "Asha Testperson",
            [SubmittedFieldKeys.BankName] = "Synthetic Test Bank",
            [SubmittedFieldKeys.AccountNumberLast4] = "2233",
            [SubmittedFieldKeys.PeriodFrom] = "2024-04-01",
            [SubmittedFieldKeys.PeriodTo] = "2024-04-30",
        };
        var p = Make(FakeDocumentParser.WithLines(SampleTexts.GenericStatement()));
        var ok = await p.ProcessDocumentAsync(Ctx.Of(VerificationType.BankStatement, fields: fields), CancellationToken.None);
        ok.Checks.Where(c => c.Code.StartsWith("BS_MATCH_", StringComparison.Ordinal) || c.Code == BankCheckCodes.SubmittedMatch).ShouldAllBe(c => c.Outcome == CheckOutcome.Pass);

        fields[SubmittedFieldKeys.AccountHolderName] = "Someone Else";
        fields[SubmittedFieldKeys.AccountNumberLast4] = "0000";
        fields[SubmittedFieldKeys.PeriodTo] = "2024-05-31";
        var bad = await p.ProcessDocumentAsync(Ctx.Of(VerificationType.BankStatement, fields: fields), CancellationToken.None);
        Ctx.C(bad, "BS_MATCH_HOLDER_NAME").Outcome.ShouldBe(CheckOutcome.Fail);
        Ctx.C(bad, "BS_MATCH_ACCOUNT_LAST4").Outcome.ShouldBe(CheckOutcome.Fail);
        Ctx.C(bad, "BS_MATCH_PERIOD").Outcome.ShouldBe(CheckOutcome.Warn);
        Ctx.C(bad, BankCheckCodes.SubmittedMatch).Outcome.ShouldBe(CheckOutcome.Fail);
        bad.Reasons.Select(r => r.Code).ShouldContain("BS_MATCH_HOLDER_NAME");

        var none = await p.ProcessDocumentAsync(Ctx.Of(VerificationType.BankStatement), CancellationToken.None);
        Ctx.C(none, BankCheckCodes.SubmittedMatch).Outcome.ShouldBe(CheckOutcome.NotEvaluated);
    }
}

public class PanProcessorTests
{
    private static PanModuleProcessor Make(FakeDocumentParser parser, FakePanVerificationProvider? provider = null, ILogger<PanModuleProcessor>? log = null)
    {
        var opts = Ctx.Opts;
        var resolver = new PanVerificationProviderResolver(
            provider is null ? opts : Options.Create(new VerificationOptions { Providers = { Pan = { Provider = provider.ProviderId, TimeoutSeconds = 1 } } }),
            provider is null ? [new NotConfiguredPanVerificationProvider()] : [provider], NullLogger<PanVerificationProviderResolver>.Instance);
        var o = provider is null ? opts : Options.Create(new VerificationOptions { Providers = { Pan = { Provider = provider.ProviderId, TimeoutSeconds = 1 } } });
        return new PanModuleProcessor(parser, new NameMatcher(o), resolver, o, log ?? NullLogger<PanModuleProcessor>.Instance);
    }

    private static Dictionary<string, string?> Fields(string? pan = SampleTexts.PanNumber, string? name = "Asha Testperson", string? dob = "1990-08-15", string? father = "Raj Testperson") => new()
    {
        [SubmittedFieldKeys.PanNumber] = pan,
        [SubmittedFieldKeys.FullName] = name,
        [SubmittedFieldKeys.DateOfBirth] = dob,
        [SubmittedFieldKeys.FatherName] = father,
    };

    [Fact]
    public async Task Matching_card_passes_every_check_but_is_only_checks_completed()
    {
        var o = await Make(FakeDocumentParser.WithLines(SampleTexts.PanCard())).ProcessDocumentAsync(Ctx.Of(VerificationType.Pan, fields: Fields()), CancellationToken.None);
        o.Extracted.ShouldBe(StageState.Completed);
        o.Reasons.ShouldBeEmpty();
        foreach (var code in new[] { "PAN_FORMAT", "PAN_HOLDER_TYPE", "PAN_DOC_NUMBER_MATCH", "PAN_NAME_MATCH", "PAN_DOB_MATCH", "PAN_FATHER_NAME_MATCH", "PAN_NAME_INITIAL", "PAN_EXTRACTION_CONFIDENCE" })
        {
            Ctx.C(o, code).Outcome.ShouldBe(CheckOutcome.Pass, code);
        }

        Ctx.C(o, "PAN_FORMAT").Message.ShouldContain("does not show that the PAN exists");
        o.Data.ShouldBeOfType<PanData>().PanNumber.ShouldBe(SampleTexts.PanNumber);
        VerificationRules.ComputeAssurance(o.Extracted, o.Rules, o.Consistency, ExternalState.ProviderNotConfigured, null, null).ShouldBe(AssuranceLevel.ChecksCompleted);
    }

    [Fact]
    public async Task Mismatches_become_review_reasons()
    {
        var fields = Fields(pan: "ABCPT9999F", name: "Different Person", dob: "1991-01-01");
        var o = await Make(FakeDocumentParser.WithLines(SampleTexts.PanCard())).ProcessDocumentAsync(Ctx.Of(VerificationType.Pan, fields: fields), CancellationToken.None);
        o.Reasons.Select(r => r.Code).ShouldBe(["PAN_DOC_NUMBER_MATCH", "PAN_NAME_MATCH", "PAN_DOB_MATCH"], ignoreOrder: true);
    }

    [Fact]
    public async Task Invalid_number_unknown_holder_and_ocr_correction()
    {
        var bad = await Make(FakeDocumentParser.WithLines(SampleTexts.PanCard(pan: "ABCXT1234F"))).ProcessDocumentAsync(Ctx.Of(VerificationType.Pan), CancellationToken.None);
        Ctx.C(bad, "PAN_HOLDER_TYPE").Outcome.ShouldBe(CheckOutcome.Fail);

        var corrected = await Make(FakeDocumentParser.WithLines(SampleTexts.PanCard(pan: "ABCPT1Z34F"))).ProcessDocumentAsync(Ctx.Of(VerificationType.Pan), CancellationToken.None);
        Ctx.C(corrected, "PAN_OCR_CORRECTED").Outcome.ShouldBe(CheckOutcome.Warn);
        corrected.Reasons.ShouldBeEmpty(); // a warning, not a review reason

        var none = await Make(FakeDocumentParser.WithLines(["Name", "ASHA TESTPERSON"])).ProcessDocumentAsync(Ctx.Of(VerificationType.Pan), CancellationToken.None);
        Ctx.C(none, "PAN_FORMAT").Outcome.ShouldBe(CheckOutcome.Fail);
    }

    [Fact]
    public async Task Number_only_request_skips_extraction()
    {
        var o = await Make(new FakeDocumentParser()).ProcessDocumentAsync(Ctx.Of(VerificationType.Pan, doc: [], fields: Fields()), CancellationToken.None);
        o.Extracted.ShouldBe(StageState.Skipped);
        o.Rules.ShouldBe(StageState.Completed);
        o.Consistency.ShouldBe(StageState.Skipped);
        Ctx.C(o, "PAN_FORMAT").Outcome.ShouldBe(CheckOutcome.Pass);

        var badNumber = await Make(new FakeDocumentParser()).ProcessDocumentAsync(Ctx.Of(VerificationType.Pan, doc: [], fields: Fields(pan: "BAD")), CancellationToken.None);
        Ctx.C(badNumber, "PAN_FORMAT").Outcome.ShouldBe(CheckOutcome.Fail);
        badNumber.Reasons.ShouldNotBeEmpty();

        var neither = await Make(new FakeDocumentParser()).ProcessDocumentAsync(Ctx.Of(VerificationType.Pan, doc: [], fields: []), CancellationToken.None);
        neither.Extracted.ShouldBe(StageState.Failed);
    }

    [Fact]
    public async Task Unreadable_document_fails_extraction()
    {
        var o = await Make(new FakeDocumentParser()).ProcessDocumentAsync(Ctx.Of(VerificationType.Pan), CancellationToken.None);
        o.Extracted.ShouldBe(StageState.Failed);
        o.FailureCode.ShouldBe("DOCUMENT_UNREADABLE");
    }

    [Fact]
    public async Task External_stage_without_a_provider_is_provider_not_configured()
    {
        var r = await Make(new FakeDocumentParser()).VerifyExternalAsync(Ctx.Of(VerificationType.Pan, fields: Fields()), CancellationToken.None);
        r.State.ShouldBe(ExternalState.ProviderNotConfigured);
        r.ProviderReference.ShouldBeNull();
        r.ReasonCode.ShouldBe("PROVIDER_NOT_CONFIGURED");
    }

    [Theory]
    [InlineData("ABCPT1234V", ExternalState.Verified)]
    [InlineData("ABCPT1234N", ExternalState.NotVerified)]
    [InlineData("ABCPT1234I", ExternalState.Inconclusive)]
    [InlineData("ABCPT1234E", ExternalState.ProviderError)]
    [InlineData("ABCPT1234T", ExternalState.Timeout)]
    public async Task External_stage_maps_scripted_provider_outcomes(string pan, ExternalState expected)
    {
        var provider = new FakePanVerificationProvider();
        var r = await Make(new FakeDocumentParser(), provider).VerifyExternalAsync(Ctx.Of(VerificationType.Pan, fields: Fields(pan: pan)), CancellationToken.None);
        r.State.ShouldBe(expected);
        if (expected == ExternalState.Verified)
        {
            r.ProviderId.ShouldBe("fake-pan");
            r.ProviderReference.ShouldNotBeNull();
            VerificationRules.ComputeAssurance(StageState.Completed, StageState.Completed, StageState.Completed, r.State, r.ProviderId, r.ProviderReference).ShouldBe(AssuranceLevel.AuthoritativelyVerified);
        }

        provider.LastRequest!.CorrelationId.ShouldBe("corr-1");
    }

    [Fact]
    public async Task Provider_is_not_called_for_a_malformed_number()
    {
        var provider = new FakePanVerificationProvider();
        var r = await Make(new FakeDocumentParser(), provider).VerifyExternalAsync(Ctx.Of(VerificationType.Pan, fields: Fields(pan: "BAD")), CancellationToken.None);
        r.State.ShouldBe(ExternalState.Inconclusive);
        provider.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Extracted_number_is_used_when_none_was_submitted()
    {
        var provider = new FakePanVerificationProvider { Mode = FakeProviderMode.Verified };
        await Make(new FakeDocumentParser(), provider).VerifyExternalAsync(
            Ctx.Of(VerificationType.Pan, fields: [], extracted: new PanData { PanNumber = SampleTexts.PanNumber }), CancellationToken.None);
        provider.LastRequest!.Pan.ShouldBe(SampleTexts.PanNumber);
    }
}

public class PassportProcessorTests
{
    private static PassportModuleProcessor Make(FakeDocumentParser parser, FakePassportVerificationProvider? provider = null, ILogger<PassportModuleProcessor>? log = null)
    {
        var o = provider is null ? Ctx.Opts : Options.Create(new VerificationOptions { Providers = { Passport = { Provider = provider.ProviderId, TimeoutSeconds = 1 } } });
        var resolver = new PassportVerificationProviderResolver(o, provider is null ? [new NotConfiguredPassportVerificationProvider()] : [provider], NullLogger<PassportVerificationProviderResolver>.Instance);
        return new PassportModuleProcessor(parser, new NameMatcher(o), resolver, o, new FixedClock(Ctx.Now), log ?? NullLogger<PassportModuleProcessor>.Instance);
    }

    private static Dictionary<string, string?> Fields() => new()
    {
        [SubmittedFieldKeys.PassportNumber] = "X1234567",
        [SubmittedFieldKeys.FullName] = "Asha Testperson",
        [SubmittedFieldKeys.DateOfBirth] = "1990-08-15",
        [SubmittedFieldKeys.DateOfExpiry] = "2035-12-31",
        [SubmittedFieldKeys.DateOfIssue] = "2026-01-01",
        [SubmittedFieldKeys.Nationality] = "UTO",
    };

    [Fact]
    public async Task Consistent_passport_page_passes_and_is_not_called_genuine()
    {
        var o = await Make(FakeDocumentParser.WithLines(SampleTexts.PassportPage())).ProcessDocumentAsync(Ctx.Of(VerificationType.Passport, fields: Fields()), CancellationToken.None);
        o.Extracted.ShouldBe(StageState.Completed);
        o.Reasons.ShouldBeEmpty(string.Join(",", o.Reasons.Select(r => r.Code)));
        var d = o.Data.ShouldBeOfType<PassportData>();
        d.MrzPresent.ShouldBeTrue();
        d.MrzFormat.ShouldBe("TD3");
        d.MrzChecks.Composite.ShouldBe(MrzCheckState.Valid);
        foreach (var code in new[] { "PP_MRZ_PRESENT", "PP_MRZ_CHECK_DOC", "PP_MRZ_CHECK_DOB", "PP_MRZ_CHECK_EXP", "PP_MRZ_CHECK_COMPOSITE", "PP_NUMBER_FORMAT", "PP_DATES_VALID", "PP_NOT_EXPIRED", "PP_ISSUE_BEFORE_EXPIRY", "PP_VALIDITY_SPAN", "PP_AGE_PLAUSIBLE", "PP_VIZ_MRZ_CONSISTENT", "PP_NAME_MATCH", "PP_NUMBER_MATCH", "PP_DOB_MATCH", "PP_EXPIRY_MATCH", "PP_ISSUE_MATCH", "PP_NATIONALITY_MATCH" })
        {
            Ctx.C(o, code).Outcome.ShouldBe(CheckOutcome.Pass, code);
        }

        Ctx.C(o, "PP_MRZ_CHECK_PERSONAL").Outcome.ShouldBe(CheckOutcome.NotEvaluated);
        Ctx.C(o, "PP_MRZ_CHECK_DOC").Message.ShouldContain("does not show that the passport is genuine");
        o.Checks.ShouldAllBe(c => !c.Message.Contains("genuine passport", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Icao_specimen_is_expired_and_invalid_digits_are_flagged()
    {
        var specimen = new[] { "Passport No L898902C3", "Surname", "ERIKSSON", SampleDocuments.IcaoSpecimenLine1, SampleDocuments.IcaoSpecimenLine2 };
        var o = await Make(FakeDocumentParser.WithLines(specimen)).ProcessDocumentAsync(Ctx.Of(VerificationType.Passport), CancellationToken.None);
        Ctx.C(o, "PP_MRZ_CHECK_COMPOSITE").Outcome.ShouldBe(CheckOutcome.Pass);
        Ctx.C(o, "PP_NOT_EXPIRED").Outcome.ShouldBe(CheckOutcome.Fail);
        o.Reasons.Select(r => r.Code).ShouldContain("PP_NOT_EXPIRED");
        o.Data.ShouldBeOfType<PassportData>().Surname.ShouldBe("ERIKSSON");

        var broken = specimen.Select(l => l == SampleDocuments.IcaoSpecimenLine2 ? l.Remove(13, 1).Insert(13, "8") : l).ToArray();
        var bad = await Make(FakeDocumentParser.WithLines(broken)).ProcessDocumentAsync(Ctx.Of(VerificationType.Passport), CancellationToken.None);
        Ctx.C(bad, "PP_MRZ_CHECK_DOB").Outcome.ShouldBe(CheckOutcome.Fail);
        Ctx.C(bad, "PP_MRZ_CHECK_COMPOSITE").Outcome.ShouldBe(CheckOutcome.Fail);
    }

    [Fact]
    public async Task Field_mismatches_and_viz_mrz_disagreement_are_reported()
    {
        var f = Fields();
        f[SubmittedFieldKeys.FullName] = "Someone Else";
        f[SubmittedFieldKeys.DateOfBirth] = "1991-01-01";
        f[SubmittedFieldKeys.PassportNumber] = "X7654321";
        var page = SampleTexts.PassportPage().Select(l => l.StartsWith("Date of Birth", StringComparison.Ordinal) ? "Date of Birth 16/08/1990" : l).ToArray();
        var o = await Make(FakeDocumentParser.WithLines(page)).ProcessDocumentAsync(Ctx.Of(VerificationType.Passport, fields: f), CancellationToken.None);
        Ctx.C(o, "PP_NAME_MATCH").Outcome.ShouldBe(CheckOutcome.Fail);
        Ctx.C(o, "PP_DOB_MATCH").Outcome.ShouldBe(CheckOutcome.Fail);
        Ctx.C(o, "PP_NUMBER_MATCH").Outcome.ShouldBe(CheckOutcome.Fail);
        Ctx.C(o, "PP_VIZ_MRZ_CONSISTENT").Outcome.ShouldBe(CheckOutcome.Fail);
    }

    [Fact]
    public async Task Without_mrz_the_visual_zone_is_used_and_a_review_signal_is_raised()
    {
        var o = await Make(FakeDocumentParser.WithLines(SampleTexts.PassportPage(withMrz: false))).ProcessDocumentAsync(Ctx.Of(VerificationType.Passport), CancellationToken.None);
        Ctx.C(o, "PP_MRZ_PRESENT").Outcome.ShouldBe(CheckOutcome.Warn);
        Ctx.C(o, "PP_MRZ_CHECK_COMPOSITE").Outcome.ShouldBe(CheckOutcome.NotEvaluated);
        Ctx.C(o, "PP_VIZ_MRZ_CONSISTENT").Outcome.ShouldBe(CheckOutcome.NotEvaluated);
        o.Data.ShouldBeOfType<PassportData>().PassportNumber.ShouldBe("X1234567");
        o.Reasons.Select(r => r.Code).ShouldContain("PP_MRZ_PRESENT");
    }

    [Fact]
    public async Task Date_rules()
    {
        var longSpan = SampleTexts.PassportPage(issue: "01/01/2020", expiry: "31/12/2035", mrzExpiry: "351231");
        var o = await Make(FakeDocumentParser.WithLines(longSpan)).ProcessDocumentAsync(Ctx.Of(VerificationType.Passport), CancellationToken.None);
        Ctx.C(o, "PP_VALIDITY_SPAN").Outcome.ShouldBe(CheckOutcome.Warn); // 16 years > MaxValidityYears

        var inverted = SampleTexts.PassportPage(issue: "01/01/2040", expiry: "31/12/2035", mrzExpiry: "351231");
        var i = await Make(FakeDocumentParser.WithLines(inverted)).ProcessDocumentAsync(Ctx.Of(VerificationType.Passport), CancellationToken.None);
        Ctx.C(i, "PP_ISSUE_BEFORE_EXPIRY").Outcome.ShouldBe(CheckOutcome.Fail);

        var young = SampleTexts.Mrz(dob: "260101");
        var future = await Make(FakeDocumentParser.WithLines([young.Line1, young.Line2])).ProcessDocumentAsync(Ctx.Of(VerificationType.Passport), CancellationToken.None);
        Ctx.C(future, "PP_AGE_PLAUSIBLE").Outcome.ShouldBe(CheckOutcome.Pass); // born Jan 2026: a baby passport is plausible
    }

    [Fact]
    public async Task Unreadable_input_fails_extraction()
    {
        var o = await Make(new FakeDocumentParser()).ProcessDocumentAsync(Ctx.Of(VerificationType.Passport), CancellationToken.None);
        o.Extracted.ShouldBe(StageState.Failed);
        (await Make(FakeDocumentParser.WithLines(["lorem", "ipsum"])).ProcessDocumentAsync(Ctx.Of(VerificationType.Passport), CancellationToken.None)).FailureCode.ShouldBe("DOCUMENT_UNREADABLE");
    }

    [Fact]
    public async Task External_stage_states()
    {
        var data = new PassportData { PassportNumber = "X1234567V", IssuingCountry = "UTO" };
        (await Make(new FakeDocumentParser()).VerifyExternalAsync(Ctx.Of(VerificationType.Passport, extracted: data), CancellationToken.None)).State.ShouldBe(ExternalState.ProviderNotConfigured);

        var provider = new FakePassportVerificationProvider();
        (await Make(new FakeDocumentParser(), provider).VerifyExternalAsync(Ctx.Of(VerificationType.Passport, extracted: data), CancellationToken.None)).State.ShouldBe(ExternalState.Verified);
        (await Make(new FakeDocumentParser(), provider).VerifyExternalAsync(Ctx.Of(VerificationType.Passport, extracted: data with { PassportNumber = "X1234567T" }), CancellationToken.None)).State.ShouldBe(ExternalState.Timeout);
        (await Make(new FakeDocumentParser(), provider).VerifyExternalAsync(Ctx.Of(VerificationType.Passport, extracted: new PassportData()), CancellationToken.None)).State.ShouldBe(ExternalState.Inconclusive);
    }
}
