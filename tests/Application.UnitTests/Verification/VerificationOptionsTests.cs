using NexaVerify.Application.Verification.Processing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.UnitTests.Verification;

public class VerificationOptionsTests
{
    private static string[] Failures(Action<VerificationOptions> change)
    {
        var options = new VerificationOptions();
        change(options);
        var result = new VerificationOptionsValidator().Validate(null, options);
        return result.Failures?.ToArray() ?? [];
    }

    [Fact]
    public void Defaults_are_valid_and_match_the_design()
    {
        new VerificationOptionsValidator().Validate(null, new VerificationOptions()).Succeeded.ShouldBeTrue();

        var o = new VerificationOptions();
        o.Upload.MaxFileBytes.ShouldBe(10_485_760);
        o.Upload.MaxPages.BankStatement.ShouldBe(30);
        o.Upload.MaxPages.Pan.ShouldBe(2);
        o.Upload.MaxPages.Passport.ShouldBe(4);
        o.Limits.MaxQueuedPerClient.ShouldBe(50);
        o.Worker.LeaseSeconds.ShouldBe(300);
        o.Processing.BackoffSeconds.ShouldBe([30, 120, 600]);
        o.Providers.Pan.Provider.ShouldBe("None");
        o.Providers.StoreRawResponse.ShouldBeFalse();
        o.RequireConsentReference.ShouldBeTrue();
        o.Retention.DocumentDays.ShouldBe(7);
        o.Download.AllowOriginalDownload.ShouldBeFalse();
        o.Download.LinkSeconds.ShouldBe(60);
    }

    [Fact]
    public void Upload_policy_is_built_per_type_from_configuration()
    {
        var o = new VerificationOptions();
        o.Upload.MaxPages.Pan = 3;

        o.UploadPolicyFor(VerificationType.Pan).MaxPages.ShouldBe(3);
        o.UploadPolicyFor(VerificationType.BankStatement).MaxPages.ShouldBe(30);
        o.UploadPolicyFor(VerificationType.Passport).MaxFileBytes.ShouldBe(o.Upload.MaxFileBytes);
    }

    [Fact]
    public void Lease_must_outlive_the_processing_budget()
    {
        Failures(o => { o.Worker.LeaseSeconds = 150; o.Processing.TimeoutSeconds = 120; }).ShouldContain(f => f.Contains("LeaseSeconds"));
        Failures(o => { o.Worker.LeaseSeconds = 151; o.Processing.TimeoutSeconds = 120; }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(91, 365, true)]
    [InlineData(90, 365, false)]
    [InlineData(7, 366, true)]
    [InlineData(0, 90, true)]
    [InlineData(30, 20, true)]
    public void Retention_is_capped(int documentDays, int resultDays, bool invalid)
    {
        Failures(o => { o.Retention.DocumentDays = documentDays; o.Retention.ResultDays = resultDays; }).Any().ShouldBe(invalid);
    }

    [Fact]
    public void Bad_values_are_reported_together_with_the_section_prefix()
    {
        var failures = Failures(o =>
        {
            o.Upload.MaxFileBytes = 0;
            o.Upload.AllowedExtensions = ["pdf"];
            o.Pan.Pattern = "([";
            o.Pan.HolderTypes = ["PP"];
            o.Names.MatchThreshold = 0.5m;
            o.Names.PartialThreshold = 0.8m;
            o.Ocr.Provider = "cloud";
            o.Providers.StoreRawResponse = true;
            o.Processing.BackoffSeconds = [];
        });

        failures.Length.ShouldBeGreaterThanOrEqualTo(8);
        failures.ShouldAllBe(f => f.StartsWith("Verification:", StringComparison.Ordinal));
    }

    [Fact]
    public void Patterns_must_compile()
    {
        Failures(o => o.Passport.NumberPatterns["IND"] = "(unclosed").ShouldContain(f => f.Contains("NumberPatterns"));
        Failures(o => o.Passport.DefaultNumberPattern = "").ShouldContain(f => f.Contains("DefaultNumberPattern"));
    }

    [Fact]
    public void Ocr_can_be_switched_off_without_tesseract_settings()
    {
        Failures(o => { o.Ocr.Provider = "none"; o.Ocr.Tesseract.ExecutablePath = ""; }).ShouldBeEmpty();
        Failures(o => o.Ocr.Tesseract.ExecutablePath = "").ShouldNotBeEmpty();
    }
}
