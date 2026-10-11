using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Application.Verification.Submission;
using NexaVerify.Contracts.Verification;

namespace NexaVerify.Application.UnitTests.Verification;

public class SubmitValidatorTests
{
    private static IOptions<VerificationOptions> Opts(bool consent = true) =>
        Microsoft.Extensions.Options.Options.Create(new VerificationOptions { RequireConsentReference = consent });

    [Fact]
    public void Bank_statement_fields_are_validated()
    {
        var v = new SubmitBankStatementRequestValidator(Opts());

        v.Validate(new SubmitBankStatementRequest("r", "c", "Holder", "Bank", "1234", new DateOnly(2025, 1, 1), new DateOnly(2025, 3, 1))).IsValid.ShouldBeTrue();
        v.Validate(new SubmitBankStatementRequest(null, null, null, null, null, null, null)).Errors.Select(e => e.PropertyName).ShouldContain("ConsentReference");
        v.Validate(new SubmitBankStatementRequest(null, "c", null, null, "12a4", null, null)).IsValid.ShouldBeFalse();
        v.Validate(new SubmitBankStatementRequest(null, "c", null, null, null, new DateOnly(2025, 3, 2), new DateOnly(2025, 3, 1))).IsValid.ShouldBeFalse();
        v.Validate(new SubmitBankStatementRequest(new string('x', 101), "c", null, null, null, null, null)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Consent_can_be_made_optional_by_configuration()
    {
        new SubmitBankStatementRequestValidator(Opts(consent: false)).Validate(new SubmitBankStatementRequest(null, null, null, null, null, null, null)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Pan_requires_a_full_name()
    {
        var v = new SubmitPanRequestValidator(Opts());

        v.Validate(new SubmitPanRequest(null, "c", "ABCDE1234F", "A Person", null, null)).IsValid.ShouldBeTrue();
        v.Validate(new SubmitPanRequest(null, "c", null, "", null, null)).Errors.Select(e => e.PropertyName).ShouldContain("FullName");
    }

    [Fact]
    public void Passport_nationality_must_be_alpha3_and_dates_ordered()
    {
        var v = new SubmitPassportRequestValidator(Opts());

        v.Validate(new SubmitPassportRequest(null, "c", "N1234567", "A Person", null, "IND", new DateOnly(2020, 1, 1), new DateOnly(2030, 1, 1))).IsValid.ShouldBeTrue();
        v.Validate(new SubmitPassportRequest(null, "c", null, null, null, "IN", null, null)).IsValid.ShouldBeFalse();
        v.Validate(new SubmitPassportRequest(null, "c", null, null, null, null, new DateOnly(2030, 1, 1), new DateOnly(2020, 1, 1))).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Download_and_module_requests_are_validated()
    {
        var d = new VerificationDownloadRequestValidator();
        d.Validate(new VerificationDownloadRequest("Report", "html")).IsValid.ShouldBeTrue();
        d.Validate(new VerificationDownloadRequest("TransactionsCsv", null)).IsValid.ShouldBeTrue();
        d.Validate(new VerificationDownloadRequest("Zip", null)).IsValid.ShouldBeFalse();
        d.Validate(new VerificationDownloadRequest("Report", "pdf")).IsValid.ShouldBeFalse();

        var m = new UpdateVerificationModuleRequestValidator();
        m.Validate(new UpdateVerificationModuleRequest(false, "maintenance", "AAAA")).IsValid.ShouldBeTrue();
        m.Validate(new UpdateVerificationModuleRequest(false, "", "AAAA")).IsValid.ShouldBeFalse();
    }
}
