using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Verification;
using NexaVerify.Infrastructure.Verification.Documents;
using NexaVerify.TestSupport.Verification;

namespace NexaVerify.Infrastructure.UnitTests.Verification;

public class DocumentInspectorTests
{
    private static readonly VerificationOptions Opts = new();

    private static DocumentInspector Inspector(DocumentPipelineOptions? pipeline = null) =>
        new(Options.Create(Opts), Options.Create(pipeline ?? new DocumentPipelineOptions()), NullLogger<DocumentInspector>.Instance);

    private static UploadPolicy Policy(VerificationType t = VerificationType.BankStatement) => Opts.UploadPolicyFor(t);

    private static Task<NexaVerify.Application.Common.Result<DocumentInspection>> Run(byte[] content, string name, string? type = null, UploadPolicy? policy = null) =>
        Inspector().InspectAsync(new DocumentUpload(name, type, content), policy ?? Policy(), CancellationToken.None);

    private static async Task ShouldFail(byte[] content, string name, string code, string? type = null, UploadPolicy? policy = null)
    {
        var r = await Run(content, name, type, policy);
        r.IsFailure.ShouldBeTrue();
        r.Error!.Code.ShouldBe(code);
    }

    [Fact]
    public async Task Digital_pdf_is_accepted_with_page_count_and_hash()
    {
        var pdf = SampleDocuments.Pdf(["page one"], ["page two"]);
        var r = await Run(pdf, "statement.PDF", "application/pdf");
        r.IsSuccess.ShouldBeTrue();
        r.Value.Kind.ShouldBe(DocumentKind.Pdf);
        r.Value.PageCount.ShouldBe(2);
        r.Value.Extension.ShouldBe(".pdf");
        r.Value.HasActiveContent.ShouldBeFalse();
        r.Value.Sha256.Length.ShouldBe(32);
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("png")]
    [InlineData("webp")]
    public async Task Images_are_accepted_with_dimensions(string kind)
    {
        var (bytes, name, type) = kind switch
        {
            "jpg" => (SampleDocuments.Jpeg(200, 100), "card.jpeg", "image/jpeg"),
            "png" => (SampleDocuments.Png(200, 100), "card.png", "image/png"),
            _ => (SampleDocuments.WebP(200, 100), "card.webp", "image/webp"),
        };
        var r = await Run(bytes, name, type);
        r.IsSuccess.ShouldBeTrue(r.Error?.Message);
        r.Value.Width.ShouldBe(200);
        r.Value.Height.ShouldBe(100);
        r.Value.PageCount.ShouldBe(1);
    }

    [Fact]
    public async Task Empty_oversized_and_wrongly_named_files_are_rejected()
    {
        await ShouldFail([], "a.pdf", ErrorCodes.DocumentRequired);
        await ShouldFail(SampleDocuments.Png(10, 10), "a.png", ErrorCodes.DocumentTooLarge, policy: Policy() with { MaxFileBytes = 10 });
        await ShouldFail(SampleDocuments.Png(10, 10), "a.exe", ErrorCodes.DocumentUnsupportedType);
        await ShouldFail(SampleDocuments.Png(10, 10), "noextension", ErrorCodes.DocumentUnsupportedType);
        await ShouldFail("MZ......"u8.ToArray(), "a.pdf", ErrorCodes.DocumentUnsupportedType); // content is not a PDF
        await ShouldFail("<html></html>"u8.ToArray(), "a.png", ErrorCodes.DocumentUnsupportedType);
    }

    [Fact]
    public async Task Extension_mime_and_content_must_agree()
    {
        await ShouldFail(SampleDocuments.Png(10, 10), "a.pdf", ErrorCodes.DocumentInvalid);
        await ShouldFail(SampleDocuments.Pdf(["x"]), "a.png", ErrorCodes.DocumentInvalid);
        await ShouldFail(SampleDocuments.Png(10, 10), "a.png", ErrorCodes.DocumentInvalid, type: "application/pdf");
        (await Run(SampleDocuments.Png(10, 10), "a.png", "application/octet-stream")).IsSuccess.ShouldBeTrue();
        (await Run(SampleDocuments.Png(10, 10), @"C:\fakepath\..\a.png")).IsSuccess.ShouldBeTrue(); // only the extension is read
    }

    [Fact]
    public async Task Polyglots_and_appended_payloads_are_rejected()
    {
        var zip = "PK\u0003\u0004malicious-payload-that-is-not-blank"u8.ToArray();
        await ShouldFail(SampleDocuments.Append(SampleDocuments.Png(20, 20), zip), "a.png", ErrorCodes.DocumentInvalid);
        await ShouldFail(SampleDocuments.Append(SampleDocuments.Jpeg(20, 20), zip), "a.jpg", ErrorCodes.DocumentInvalid);
        await ShouldFail(SampleDocuments.Append(SampleDocuments.WebP(20, 20), zip), "a.webp", ErrorCodes.DocumentInvalid);
        await ShouldFail(SampleDocuments.Append(SampleDocuments.Pdf(["x"]), zip), "a.pdf", ErrorCodes.DocumentInvalid);
        await ShouldFail([.. "GIF89a-or-script\n"u8, .. SampleDocuments.Pdf(["x"])], "a.pdf", ErrorCodes.DocumentUnsupportedType); // header not at offset 0
        (await Run(SampleDocuments.Append(SampleDocuments.Png(20, 20), new byte[16]), "a.png")).IsSuccess.ShouldBeTrue(); // blank padding is tolerated
    }

    [Fact]
    public async Task Truncated_images_are_rejected()
    {
        var png = SampleDocuments.Png(50, 50);
        await ShouldFail(png[..(png.Length / 2)], "a.png", ErrorCodes.DocumentInvalid);
        var jpg = SampleDocuments.Jpeg(50, 50);
        await ShouldFail(jpg[..(jpg.Length / 2)], "a.jpg", ErrorCodes.DocumentInvalid);
    }

    [Fact]
    public async Task Decompression_bomb_dimensions_are_rejected_before_decoding()
    {
        await ShouldFail(SampleDocuments.PngBomb(60000, 60000), "a.png", ErrorCodes.DocumentTooLarge);
    }

    [Fact]
    public async Task Too_many_pages_is_rejected()
    {
        await ShouldFail(SampleDocuments.BlankPdf(3), "a.pdf", ErrorCodes.DocumentTooManyPages, policy: Policy() with { MaxPages = 2 });
        (await Run(SampleDocuments.BlankPdf(2), "a.pdf", policy: Policy() with { MaxPages = 2 })).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("/OpenAction << /S /JavaScript /JS (app.alert(1)) >>")]
    [InlineData("/OpenAction << /S /Launch /F (cmd.exe) >>")]
    [InlineData("/Names << /JavaScript << /Names [(a) 9 0 R] >> >>")]
    [InlineData("/Names << /EmbeddedFiles << /Names [(a.exe) 9 0 R] >> >>")]
    [InlineData("/AcroForm << /XFA [(x) 9 0 R] >>")]
    [InlineData("/AA << /O << /S /JavaScript /JS (x) >> >>")]
    public async Task Pdf_active_content_is_rejected(string catalogExtra) =>
        await ShouldFail(SampleDocuments.RawPdf(catalogExtra), "a.pdf", ErrorCodes.DocumentActiveContent);

    [Fact]
    public async Task Pdf_active_content_with_hex_escaped_names_is_rejected()
    {
        await ShouldFail(SampleDocuments.RawPdf("/OpenAction << /S /J#61vaScript /JS (x) >>"), "a.pdf", ErrorCodes.DocumentActiveContent);
        await ShouldFail(SampleDocuments.RawPdf(pageExtra: "/AA << /O << /S /Launch >> >>"), "a.pdf", ErrorCodes.DocumentActiveContent);
    }

    [Fact]
    public async Task Active_content_can_be_allowed_by_policy_and_is_reported()
    {
        var r = await Run(SampleDocuments.RawPdf("/OpenAction << /S /JavaScript /JS (x) >>"), "a.pdf", policy: Policy() with { RejectActiveContent = false });
        r.IsSuccess.ShouldBeTrue();
        r.Value.HasActiveContent.ShouldBeTrue();
    }

    [Fact]
    public async Task Clean_raw_pdf_is_accepted()
    {
        (await Run(SampleDocuments.RawPdf(), "a.pdf")).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Encrypted_pdf_is_rejected()
    {
        await ShouldFail(SampleDocuments.RawPdf(trailerExtra: "/Encrypt << /Filter /Standard /V 1 /R 2 /O (aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa) /U (bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb) /P -4 >> /ID [<00> <00>]"), "a.pdf", ErrorCodes.DocumentEncrypted);
    }

    [Fact]
    public async Task Corrupt_pdf_and_unreadable_images_are_rejected_without_leaking_details()
    {
        var r = await Run("%PDF-1.4\nthis is not a pdf at all\n%%EOF\n"u8.ToArray(), "a.pdf");
        r.IsFailure.ShouldBeTrue();
        r.Error!.Code.ShouldBeOneOf(ErrorCodes.DocumentInvalid, ErrorCodes.DocumentEncrypted);
        r.Error.Message.ShouldNotContain("Exception");
        await ShouldFail([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0xFF, 0xD9], "a.jpg", ErrorCodes.DocumentInvalid);
        await ShouldFail("%PDF-1.4 no end marker at all"u8.ToArray(), "a.pdf", ErrorCodes.DocumentInvalid);
    }

    [Fact]
    public void Sniffing_is_by_magic_bytes_only()
    {
        DocumentInspector.Sniff("%PDF-1.7"u8).ShouldBe(DocumentKind.Pdf);
        DocumentInspector.Sniff([0xFF, 0xD8, 0xFF]).ShouldBe(DocumentKind.Jpeg);
        DocumentInspector.Sniff([]).ShouldBeNull();
        DocumentInspector.Sniff(" %PDF-1.7"u8).ShouldBeNull();
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            Inspector().InspectAsync(new DocumentUpload("a.png", null, SampleDocuments.Png(10, 10)), Policy(), cts.Token));
    }
}
