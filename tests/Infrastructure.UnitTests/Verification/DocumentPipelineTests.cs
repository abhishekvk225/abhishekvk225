using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Verification;
using NexaVerify.Infrastructure.Verification.Documents;
using NexaVerify.Infrastructure.Verification.Ocr;
using NexaVerify.TestSupport.Verification;
using SkiaSharp;
using System.Runtime.Versioning;

namespace NexaVerify.Infrastructure.UnitTests.Verification;

/// <summary>The REAL PdfPig, PDFium and Skia on generated documents (these run everywhere the native assets exist).</summary>
public class RealLibraryTests
{
    private static readonly IOptions<VerificationOptions> Opts = Options.Create(new VerificationOptions());
    private static readonly IOptions<DocumentPipelineOptions> Pipe = Options.Create(new DocumentPipelineOptions());

    [Fact]
    public async Task PdfPig_extracts_lines_in_reading_order_with_column_gaps()
    {
        var pdf = SampleDocuments.Pdf(["Account Holder: ASHA TESTPERSON", "02/04/2024 UPI PAYEE 500.00 9,500.00"], ["second page line"]);
        var pages = await new PdfPigTextExtractor().ExtractAsync(pdf, 10, CancellationToken.None);
        pages.Count.ShouldBe(2);
        pages[0].Lines.Select(l => l.Text).ShouldBe(["Account Holder: ASHA TESTPERSON", "02/04/2024 UPI PAYEE 500.00 9,500.00"]);
        pages[1].Lines.Single().Text.ShouldBe("second page line");
        (await new PdfPigTextExtractor().ExtractAsync(pdf, 1, CancellationToken.None)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task PdfPig_extractor_fails_on_garbage_and_honours_cancellation()
    {
        await Should.ThrowAsync<Exception>(() => new PdfPigTextExtractor().ExtractAsync("not a pdf"u8.ToArray(), 1, CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => new PdfPigTextExtractor().ExtractAsync(SampleDocuments.Pdf(["x"]), 1, cts.Token));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task PDFium_renders_pages_to_png_and_respects_the_pixel_cap()
    {
        using var rasterizer = new PdfiumRasterizer(Opts);
        var png = await rasterizer.RenderPageAsync(SampleDocuments.Pdf(["x"]), 1, 100, CancellationToken.None);
        using var bitmap = SKBitmap.Decode(png);
        bitmap.ShouldNotBeNull();
        bitmap.Width.ShouldBeInRange(800, 840); // A4 at 100 dpi
        (await rasterizer.RenderPagesAsync(SampleDocuments.BlankPdf(3), 2, 50, CancellationToken.None)).Count.ShouldBe(2);

        var tight = Options.Create(new VerificationOptions { Upload = { MaxImagePixels = 1000 } });
        using var capped = new PdfiumRasterizer(tight);
        await Should.ThrowAsync<InvalidOperationException>(() => capped.RenderPageAsync(SampleDocuments.Pdf(["x"]), 1, 300, CancellationToken.None));
        await Should.ThrowAsync<Exception>(() => rasterizer.RenderPageAsync("garbage"u8.ToArray(), 1, 100, CancellationToken.None));
    }

    [Fact]
    public async Task Preprocessor_produces_a_bounded_grayscale_png_and_rejects_bad_input()
    {
        var pre = new SkiaDocumentImagePreprocessor(Opts, Options.Create(new DocumentPipelineOptions { OcrMaxImageSide = 500 }));
        var result = await pre.PrepareForOcrAsync(SampleDocuments.Jpeg(1500, 1000), CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();
        using var bitmap = SKBitmap.Decode(result.Value);
        Math.Max(bitmap.Width, bitmap.Height).ShouldBe(500);
        var px = bitmap.GetPixel(10, 10);
        px.Red.ShouldBe(px.Green);
        px.Green.ShouldBe(px.Blue);

        (await pre.PrepareForOcrAsync(SampleDocuments.Png(120, 80), CancellationToken.None)).IsSuccess.ShouldBeTrue(); // PNG with an alpha channel
        (await pre.PrepareForOcrAsync("garbage"u8.ToArray(), CancellationToken.None)).Error!.Code.ShouldBe(ErrorCodes.DocumentUnreadable);
        var tight = new SkiaDocumentImagePreprocessor(Options.Create(new VerificationOptions { Upload = { MaxImagePixels = 100 } }), Pipe);
        (await tight.PrepareForOcrAsync(SampleDocuments.Png(200, 200), CancellationToken.None)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Digital_pdf_goes_through_the_real_parser_without_ocr()
    {
        var ocr = new FakeOcrProvider { IsAvailable = false };
        var parser = new DocumentParser(new PdfPigTextExtractor(), new FakePdfRasterizer(), new PassThroughImagePreprocessor(), ocr, NullLogger<DocumentParser>.Instance);
        var pdf = SampleDocuments.Pdf(SampleTexts.GenericStatement());
        var r = await parser.ParseAsync(pdf, DocumentKind.Pdf, new ParseOptions(5, "eng", 150, 40), CancellationToken.None);
        r.IsSuccess.ShouldBeTrue(r.Error?.Message);
        r.Value.Source.ShouldBe(TextSource.DigitalPdf);
        r.Value.MeanConfidence.ShouldBeNull();
        ocr.Calls.ShouldBe(0);
        r.Value.Pages[0].Lines.Select(l => l.Text).ShouldContain("Opening Balance: 10,000.00");
    }

    [Fact]
    public async Task Full_pipeline_real_pdf_to_real_bank_parser()
    {
        var parser = new DocumentParser(new PdfPigTextExtractor(), new FakePdfRasterizer(), new PassThroughImagePreprocessor(), new FakeOcrProvider(), NullLogger<DocumentParser>.Instance);
        var text = await parser.ParseAsync(SampleDocuments.Pdf(SampleTexts.GenericStatement()), DocumentKind.Pdf, new ParseOptions(5, "eng", 150, 40), CancellationToken.None);
        var rules = new NexaVerify.Application.Verification.BankStatements.BankRuleSetProvider(Opts);
        var parsed = new NexaVerify.Application.Verification.BankStatements.BankStatementParser(Opts, TimeProvider.System).Parse(text.Value, rules.Detect(text.Value));
        parsed.Data.Transactions.Count.ShouldBe(4);
        parsed.Data.ClosingBalance.ShouldBe(20500m);
    }
}

public class DocumentParserTests
{
    private static DocumentParser Parser(IOcrProvider ocr, FakePdfTextExtractor? text = null, FakePdfRasterizer? raster = null) =>
        new(text ?? new FakePdfTextExtractor(), raster ?? new FakePdfRasterizer(), new PassThroughImagePreprocessor(), ocr, NullLogger<DocumentParser>.Instance);

    private static readonly ParseOptions Po = new(5, "eng", 150, 40);

    private static PageText Page(int n, string text) => new(n, [new TextLine(text, null, null, null, null, null)]);

    [Fact]
    public async Task Scanned_pdf_pages_are_rasterised_and_ocr_ed_while_digital_pages_keep_their_text()
    {
        var ocr = new FakeOcrProvider().ReturnsByDefault(0.8m, "OCR LINE");
        var raster = new FakePdfRasterizer();
        var extractor = new FakePdfTextExtractor { Pages = [Page(1, new string('a', 100)), Page(2, "x"), Page(3, new string('b', 100))] };
        var r = await Parser(ocr, extractor, raster).ParseAsync(new byte[] { 1 }, DocumentKind.Pdf, Po, CancellationToken.None);
        r.IsSuccess.ShouldBeTrue();
        r.Value.Source.ShouldBe(TextSource.Mixed);
        r.Value.MeanConfidence.ShouldBe(0.8m);
        raster.RenderedPages.ShouldBe([2]); // only the scanned page
        r.Value.Pages[1].Lines.Single().Text.ShouldBe("OCR LINE");
        r.Value.Pages[0].Lines.Single().Text.Length.ShouldBe(100);
    }

    [Fact]
    public async Task All_scanned_pdf_is_pure_ocr()
    {
        var r = await Parser(new FakeOcrProvider().ReturnsByDefault(0.9m, "A"), new FakePdfTextExtractor { Pages = [Page(1, "")] })
            .ParseAsync(new byte[] { 1 }, DocumentKind.Pdf, Po, CancellationToken.None);
        r.Value.Source.ShouldBe(TextSource.Ocr);
    }

    [Fact]
    public async Task Missing_ocr_for_a_scanned_page_fails_with_ocr_unavailable_not_empty_text()
    {
        var r = await Parser(new FakeOcrProvider { IsAvailable = false }, new FakePdfTextExtractor { Pages = [Page(1, "")] })
            .ParseAsync(new byte[] { 1 }, DocumentKind.Pdf, Po, CancellationToken.None);
        r.IsFailure.ShouldBeTrue();
        r.Error!.Code.ShouldBe(ErrorCodes.OcrUnavailable);

        var img = await Parser(new FakeOcrProvider { IsAvailable = false }).ParseAsync(SampleDocuments.Png(20, 20), DocumentKind.Png, Po, CancellationToken.None);
        img.Error!.Code.ShouldBe(ErrorCodes.OcrUnavailable);
    }

    [Fact]
    public async Task Ocr_that_throws_unavailable_mid_flight_maps_to_ocr_unavailable()
    {
        var ocr = new ThrowingOcr();
        var r = await Parser(ocr).ParseAsync(SampleDocuments.Png(20, 20), DocumentKind.Png, Po, CancellationToken.None);
        r.Error!.Code.ShouldBe(ErrorCodes.OcrUnavailable);
    }

    private sealed class ThrowingOcr : IOcrProvider
    {
        public string ProviderId => "t";

        public bool IsAvailable => true;

        public Task<OcrPage> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) => throw new OcrUnavailableException("crash");
    }

    [Fact]
    public async Task Images_use_ocr_and_empty_results_are_unreadable()
    {
        var img = SampleDocuments.Png(30, 30);
        var ok = await Parser(new FakeOcrProvider().Returns(img, "PAN LINE")).ParseAsync(img, DocumentKind.Png, Po, CancellationToken.None);
        ok.Value.Pages.Single().Lines.Single().Text.ShouldBe("PAN LINE");
        ok.Value.Source.ShouldBe(TextSource.Ocr);

        var empty = await Parser(new FakeOcrProvider()).ParseAsync(img, DocumentKind.Png, Po, CancellationToken.None);
        empty.Error!.Code.ShouldBe(ErrorCodes.DocumentUnreadable);
    }

    [Fact]
    public async Task Corrupt_pdf_and_empty_input_are_unreadable()
    {
        (await Parser(new FakeOcrProvider(), new FakePdfTextExtractor { Throws = true }).ParseAsync(new byte[] { 1 }, DocumentKind.Pdf, Po, CancellationToken.None))
            .Error!.Code.ShouldBe(ErrorCodes.DocumentUnreadable);
        (await Parser(new FakeOcrProvider()).ParseAsync(ReadOnlyMemory<byte>.Empty, DocumentKind.Pdf, Po, CancellationToken.None))
            .Error!.Code.ShouldBe(ErrorCodes.DocumentUnreadable);
    }
}

public class TesseractTests
{
    private const string Tsv =
        "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
        "1\t1\t0\t0\t0\t0\t0\t0\t800\t600\t-1\t\n" +
        "5\t1\t1\t1\t1\t1\t10\t10\t50\t12\t96.5\tHello\n" +
        "5\t1\t1\t1\t1\t2\t70\t10\t50\t12\t90.0\tworld\n" +
        "5\t1\t1\t1\t2\t1\t10\t30\t80\t12\t80\tSecond\n" +
        "5\t1\t1\t1\t3\t1\t10\t50\t80\t12\t-1\tnoise\n";

    [Fact]
    public void Tsv_is_grouped_into_lines_with_scaled_confidence()
    {
        var page = TesseractCliOcrProvider.ParseTsv(Tsv, 3);
        page.PageNumber.ShouldBe(3);
        page.Lines.Select(l => l.Text).ShouldBe(["Hello world", "Second"]);
        page.Lines[0].Confidence.ShouldBe(0.9325m);
        page.Lines[0].X.ShouldBe(10);
        page.Lines[0].Width.ShouldBe(110);
        page.MeanConfidence!.Value.ShouldBe(0.8883m, 0.0001m);
    }

    [Fact]
    public void Malformed_tsv_yields_an_empty_page()
    {
        TesseractCliOcrProvider.ParseTsv("garbage\n5\tx\n", 1).Lines.ShouldBeEmpty();
        TesseractCliOcrProvider.ParseTsv(string.Empty, 1).MeanConfidence.ShouldBeNull();
    }

    private static TesseractCliOcrProvider Provider(string exe)
    {
        var o = new VerificationOptions();
        o.Ocr.Tesseract.ExecutablePath = exe;
        return new TesseractCliOcrProvider(Options.Create(o), Options.Create(new DocumentPipelineOptions()), NullLogger<TesseractCliOcrProvider>.Instance, TimeProvider.System);
    }

    [Fact]
    public async Task Missing_binary_means_unavailable_and_a_clear_exception()
    {
        using var p = Provider("/definitely/not/installed/tesseract");
        p.IsAvailable.ShouldBeFalse();
        await Should.ThrowAsync<OcrUnavailableException>(() => p.RecognizeAsync(new OcrRequest(SampleDocuments.Png(10, 10), "eng", 1), CancellationToken.None));
    }

    [Fact]
    public async Task Option_injection_through_the_language_is_impossible()
    {
        using var p = Provider("/definitely/not/installed/tesseract");
        await Should.ThrowAsync<OcrUnavailableException>(() => p.RecognizeAsync(new OcrRequest(SampleDocuments.Png(10, 10), "eng --oem 0 -c x=y", 1), CancellationToken.None));
    }

    [Fact]
    public async Task Null_provider_is_never_available()
    {
        var p = new NullOcrProvider();
        p.IsAvailable.ShouldBeFalse();
        await Should.ThrowAsync<OcrUnavailableException>(() => p.RecognizeAsync(new OcrRequest([], "eng", 1), CancellationToken.None));
    }

    [TesseractFact]
    public async Task Real_tesseract_reads_a_rendered_word()
    {
        using var p = Provider("tesseract");
        p.IsAvailable.ShouldBeTrue();
        using var bitmap = new SKBitmap(600, 160);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var font = new SKFont { Size = 80 };
            using var paint = new SKPaint { Color = SKColors.Black };
            canvas.DrawText("SYNTHETIC", 20, 110, SKTextAlign.Left, font, paint);
        }

        using var img = SKImage.FromBitmap(bitmap);
        var png = img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        var page = await p.RecognizeAsync(new OcrRequest(png, "eng", 1), CancellationToken.None);
        string.Join(' ', page.Lines.Select(l => l.Text)).ToUpperInvariant().ShouldContain("SYNTHETIC");
    }
}

/// <summary>Skipped (with a reason) when the tesseract binary or its English data is not installed on the machine running the tests.</summary>
public sealed class TesseractFactAttribute : FactAttribute
{
    public TesseractFactAttribute()
    {
        var o = new VerificationOptions();
        o.Ocr.Tesseract.ExecutablePath = "tesseract";
        using var p = new TesseractCliOcrProvider(Options.Create(o), Options.Create(new DocumentPipelineOptions()), NullLogger<TesseractCliOcrProvider>.Instance, TimeProvider.System);
        if (!p.IsAvailable)
        {
            Skip = "The 'tesseract' binary with the 'eng' language data is not installed in this environment; the real OCR path was not exercised.";
        }
    }
}
