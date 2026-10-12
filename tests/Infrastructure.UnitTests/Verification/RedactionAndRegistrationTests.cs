using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.BankStatements;
using NexaVerify.Application.Verification.Matching;
using NexaVerify.Application.Verification.Modules;
using NexaVerify.Application.Verification.Pan;
using NexaVerify.Application.Verification.Passport;
using NexaVerify.Application.Verification.Parsing;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Domain.Verification;
using NexaVerify.Infrastructure.Verification;
using NexaVerify.Infrastructure.Verification.Documents;
using NexaVerify.Infrastructure.Verification.Providers;
using NexaVerify.TestSupport.Verification;
using SkiaSharp;

namespace NexaVerify.Infrastructure.UnitTests.Verification;

public class RedactionTests
{
    private static readonly string[] Canaries =
    [
        "ASHA TESTPERSON", "Asha Testperson", "RAJ TESTPERSON", SampleTexts.PanNumber, SampleTexts.AccountNumber, "X1234567", "SYNTHETIC EMPLOYER",
        "UPI SYNTH PAYEE", "ATM WITHDRAWAL", "31/12/2035", "15/08/1990", "sensitive-canary", "Synthetic Test Bank", "TESTPERSON<<ASHA",
    ];

    private static void NoCanary(string logs)
    {
        foreach (var c in Canaries)
        {
            logs.ShouldNotContain(c, Case.Insensitive);
        }
    }

    [Fact]
    public async Task Processors_log_codes_and_counts_only()
    {
        using var capture = new LogCapture();
        using var factory = capture.Factory();
        var opts = Ctx.Opts;
        var names = new NameMatcher(opts);
        var fields = new Dictionary<string, string?>
        {
            [SubmittedFieldKeys.AccountHolderName] = "Asha Testperson", [SubmittedFieldKeys.PanNumber] = SampleTexts.PanNumber, [SubmittedFieldKeys.FullName] = "Asha Testperson",
            [SubmittedFieldKeys.PassportNumber] = "X1234567", [SubmittedFieldKeys.AccountNumberLast4] = "2233",
        };

        var bank = new BankStatementModuleProcessor(
            FakeDocumentParser.WithLines(SampleTexts.GenericStatement()), new BankRuleSetProvider(opts), new BankStatementParser(opts, TimeProvider.System), names, opts,
            factory.CreateLogger<BankStatementModuleProcessor>());
        await bank.ProcessDocumentAsync(Ctx.Of(VerificationType.BankStatement, fields: fields), CancellationToken.None);

        var panProvider = new FakePanVerificationProvider { Mode = FakeProviderMode.Throws };
        var panOpts = Options.Create(new VerificationOptions { Providers = { Pan = { Provider = "fake-pan", TimeoutSeconds = 1 } } });
        var pan = new PanModuleProcessor(
            FakeDocumentParser.WithLines(SampleTexts.PanCard()), names,
            new PanVerificationProviderResolver(panOpts, [panProvider], NullLogger<PanVerificationProviderResolver>.Instance), panOpts, factory.CreateLogger<PanModuleProcessor>());
        await pan.ProcessDocumentAsync(Ctx.Of(VerificationType.Pan, fields: fields), CancellationToken.None);
        var external = await pan.VerifyExternalAsync(Ctx.Of(VerificationType.Pan, fields: fields), CancellationToken.None);
        external.State.ShouldBe(ExternalState.ProviderError);

        var ppProvider = new FakePassportVerificationProvider { Mode = FakeProviderMode.Throws };
        var ppOpts = Options.Create(new VerificationOptions { Providers = { Passport = { Provider = "fake-passport", TimeoutSeconds = 1 } } });
        var pp = new PassportModuleProcessor(
            FakeDocumentParser.WithLines(SampleTexts.PassportPage()), names,
            new PassportVerificationProviderResolver(ppOpts, [ppProvider], NullLogger<PassportVerificationProviderResolver>.Instance), ppOpts, TimeProvider.System,
            factory.CreateLogger<PassportModuleProcessor>());
        var doc = await pp.ProcessDocumentAsync(Ctx.Of(VerificationType.Passport, fields: fields), CancellationToken.None);
        await pp.VerifyExternalAsync(Ctx.Of(VerificationType.Passport, fields: fields, extracted: doc.Data), CancellationToken.None);

        capture.Messages.ShouldNotBeEmpty();
        NoCanary(capture.All);
    }

    [Fact]
    public async Task Parser_and_inspector_failures_do_not_log_content()
    {
        using var capture = new LogCapture();
        using var factory = capture.Factory();
        var parser = new DocumentParser(new FakePdfTextExtractor { Throws = true }, new FakePdfRasterizer(), new PassThroughImagePreprocessor(), new FakeOcrProvider(), factory.CreateLogger<DocumentParser>());
        await parser.ParseAsync(new byte[] { 1 }, DocumentKind.Pdf, new ParseOptions(1, "eng", 100, 1), CancellationToken.None);
        using var inspector = new DocumentInspector(Ctx.Opts, Options.Create(new DocumentPipelineOptions()), factory.CreateLogger<DocumentInspector>());
        await inspector.InspectAsync(new DocumentUpload("ASHA TESTPERSON statement.pdf", null, "%PDF-1.4 ASHA TESTPERSON garbage\n%%EOF\n"u8.ToArray()), Ctx.Opts.Value.UploadPolicyFor(VerificationType.BankStatement), CancellationToken.None);
        NoCanary(capture.All);
        capture.All.ShouldNotContain("statement.pdf"); // not even the file name
    }

    [Fact]
    public void Value_records_never_print_their_content()
    {
        var ctx = Ctx.Of(VerificationType.Pan, fields: new() { ["panNumber"] = SampleTexts.PanNumber });
        ctx.ToString().ShouldBe(nameof(ModuleContext));
        new PanData { PanNumber = SampleTexts.PanNumber, NameOnCard = "ASHA TESTPERSON" }.ToString().ShouldBe(nameof(PanData));
        new PassportData { PassportNumber = "X1234567" }.ToString().ShouldBe(nameof(PassportData));
        new BankStatementData { AccountHolderName = "ASHA" }.ToString().ShouldBe(nameof(BankStatementData));
        new DocumentUpload("ASHA.pdf", null, [1]).ToString().ShouldNotContain("ASHA");
        new PanProviderRequest(SampleTexts.PanNumber, "ASHA", null, "c").ToString().ShouldNotContain("ABCPT");
        new TextLine("ASHA TESTPERSON", null, null, null, null, null).ToString().ShouldNotContain("ASHA");
        new DocumentStageOutcome(StageState.Completed, StageState.Completed, StageState.Completed, new PanData { PanNumber = "X" }, [], [], null).ToString().ShouldNotContain("X");
    }
}

public class RegistrationTests
{
    private static ServiceProvider Build(Dictionary<string, string?>? config = null)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerificationApplication();
        services.AddVerificationInfrastructure(cfg);
        services.AddVerificationProcessing(cfg);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });
    }

    [Fact]
    public void Entry_point_registers_the_three_processors_parser_and_not_configured_providers()
    {
        using var sp = Build();
        sp.GetServices<IModuleProcessor>().Select(p => p.Type).Order().ShouldBe([VerificationType.BankStatement, VerificationType.Pan, VerificationType.Passport]);
        sp.GetRequiredService<IDocumentParser>().ShouldBeOfType<DocumentParser>();
        sp.GetRequiredService<IDocumentInspector>().ShouldBeOfType<DocumentInspector>();
        sp.GetRequiredService<IPanVerificationProviderResolver>().Resolve().IsConfigured.ShouldBeFalse();
        sp.GetRequiredService<IPassportVerificationProviderResolver>().Resolve().IsConfigured.ShouldBeFalse();
        sp.GetRequiredService<IBankRuleSetProvider>().RuleSets.Select(r => r.Id).ShouldContain("generic");
        sp.GetRequiredService<IOptions<VerificationOptions>>().Value.ShouldNotBeNull();
        sp.GetRequiredService<IOptions<DocumentPipelineOptions>>().Value.ShouldNotBeNull();
    }

    [Fact]
    public void Module_catalogue_is_not_touched_by_the_processing_registration()
    {
        var cfg = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerificationApplication();
        var before = services.Count(d => d.ServiceType == typeof(IVerificationModule));
        services.AddVerificationInfrastructure(cfg);
        services.AddVerificationProcessing(cfg);
        services.Count(d => d.ServiceType == typeof(IVerificationModule)).ShouldBe(before);
        services.Any(d => d.ServiceType.Name.StartsWith("Fake", StringComparison.Ordinal) || (d.ImplementationType?.Name.StartsWith("Fake", StringComparison.Ordinal) ?? false)).ShouldBeFalse();
    }

    [Fact]
    public void Ocr_provider_follows_configuration_and_pipeline_options_are_validated()
    {
        using var none = Build(new() { ["Verification:Ocr:Provider"] = "none" });
        none.GetRequiredService<IOcrProvider>().IsAvailable.ShouldBeFalse();
        none.GetRequiredService<IOcrProvider>().ProviderId.ShouldBe("none");
        using var tess = Build(new() { ["Verification:Ocr:Provider"] = "tesseract-cli", ["Verification:Ocr:Tesseract:ExecutablePath"] = "/nope/tesseract" });
        tess.GetRequiredService<IOcrProvider>().ProviderId.ShouldBe("tesseract-cli");
        tess.GetRequiredService<IOcrProvider>().IsAvailable.ShouldBeFalse();

        using var bad = Build(new() { ["Verification:DocumentPipeline:OcrMaxImageSide"] = "0" });
        Should.Throw<OptionsValidationException>(() => _ = bad.GetRequiredService<IOptions<DocumentPipelineOptions>>().Value);
    }
}

public class EndToEndTests
{
    /// <summary>Renders text lines into a PNG the way a scan would look (black on white).</summary>
    private static byte[] Render(params string[] lines)
    {
        using var bitmap = new SKBitmap(1600, 120 + (lines.Length * 90));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var font = new SKFont { Size = 52 };
            using var paint = new SKPaint { Color = SKColors.Black };
            for (var i = 0; i < lines.Length; i++)
            {
                canvas.DrawText(lines[i], 40, 100 + (i * 90), SKTextAlign.Left, font, paint);
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    [TesseractFact]
    public async Task Scanned_pan_card_image_through_real_inspector_preprocessor_tesseract_and_pan_processor()
    {
        using var sp = new ServiceCollection()
            .AddLogging()
            .AddVerificationApplication()
            .AddVerificationInfrastructure(new ConfigurationBuilder().Build())
            .AddVerificationProcessing(new ConfigurationBuilder().Build())
            .BuildServiceProvider();

        var png = Render("INCOME TAX DEPARTMENT", "Permanent Account Number", SampleTexts.PanNumber, "Name", "ASHA TESTPERSON", "Date of Birth", "15/08/1990");
        var opts = sp.GetRequiredService<IOptions<VerificationOptions>>().Value;
        var inspection = await sp.GetRequiredService<IDocumentInspector>().InspectAsync(new DocumentUpload("card.png", "image/png", png), opts.UploadPolicyFor(VerificationType.Pan), CancellationToken.None);
        inspection.IsSuccess.ShouldBeTrue();

        var processor = sp.GetServices<IModuleProcessor>().Single(p => p.Type == VerificationType.Pan);
        var fields = new Dictionary<string, string?> { [SubmittedFieldKeys.PanNumber] = SampleTexts.PanNumber, [SubmittedFieldKeys.FullName] = "Asha Testperson" };
        var outcome = await processor.ProcessDocumentAsync(Ctx.Of(VerificationType.Pan, png, DocumentKind.Png, fields), CancellationToken.None);

        outcome.Extracted.ShouldBe(StageState.Completed, outcome.FailureCode);
        var data = outcome.Data.ShouldBeOfType<PanData>();
        data.PanNumber.ShouldBe(SampleTexts.PanNumber);
        Ctx.C(outcome, "PAN_NAME_MATCH").Outcome.ShouldBeOneOf(CheckOutcome.Pass, CheckOutcome.Warn);
    }

    [Fact]
    public async Task Digital_pdf_statement_through_real_inspector_pdfpig_parser_and_bank_processor_without_ocr()
    {
        using var sp = new ServiceCollection()
            .AddLogging()
            .AddVerificationApplication()
            .AddVerificationInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Verification:Ocr:Provider"] = "none" }).Build())
            .AddVerificationProcessing(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Verification:Ocr:Provider"] = "none" }).Build())
            .BuildServiceProvider();
        var pdf = SampleDocuments.Pdf(SampleTexts.GenericStatement());
        var opts = sp.GetRequiredService<IOptions<VerificationOptions>>().Value;
        (await sp.GetRequiredService<IDocumentInspector>().InspectAsync(new DocumentUpload("s.pdf", "application/pdf", pdf), opts.UploadPolicyFor(VerificationType.BankStatement), CancellationToken.None)).IsSuccess.ShouldBeTrue();

        var outcome = await sp.GetServices<IModuleProcessor>().Single(p => p.Type == VerificationType.BankStatement)
            .ProcessDocumentAsync(Ctx.Of(VerificationType.BankStatement, pdf), CancellationToken.None);
        outcome.Extracted.ShouldBe(StageState.Completed, outcome.FailureCode);
        outcome.Reasons.ShouldBeEmpty();

        // A scanned (text-less) PDF with OCR disabled fails cleanly instead of "succeeding" with nothing.
        var scanned = await sp.GetServices<IModuleProcessor>().Single(p => p.Type == VerificationType.BankStatement)
            .ProcessDocumentAsync(Ctx.Of(VerificationType.BankStatement, SampleDocuments.BlankPdf()), CancellationToken.None);
        scanned.Extracted.ShouldBe(StageState.Failed);
        scanned.FailureCode.ShouldBe("OCR_UNAVAILABLE");
    }
}
