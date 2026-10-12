using NexaVerify.Application.Common;
using NexaVerify.Application.Verification.Abstractions;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Fonts.Standard14Fonts;

namespace NexaVerify.TestSupport.Verification;

/// <summary>
/// Synthetic documents built in code. No real document, PAN, passport or account exists in this repository: every value below is
/// invented (or is the public ICAO 9303 specimen). Names are deliberately fictional.
/// </summary>
public static class SampleDocuments
{
    /// <summary>ICAO 9303 specimen (Utopia, "Anna Maria Eriksson"), the published example for TD3. All five check digits are valid.</summary>
    public const string IcaoSpecimenLine1 = "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<";

    public const string IcaoSpecimenLine2 = "L898902C36UTO7408122F1204159ZE184226B<<<<<10";

    // ---- text ---------------------------------------------------------------------------------------------------------------

    public static Result<DocumentText> Text(IEnumerable<string> lines, decimal? confidence = null, TextSource source = TextSource.DigitalPdf) =>
        new DocumentText(
            [new PageText(1, lines.Select(l => new TextLine(l, confidence, null, null, null, null)).ToList())], source, confidence);

    public static DocumentText TextValue(params string[] lines) => Text(lines).Value;

    // ---- PDFs ---------------------------------------------------------------------------------------------------------------

    /// <summary>A digital PDF; every inner array is one page, every string one line of text (Helvetica, A4).</summary>
    public static byte[] Pdf(params string[][] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            var y = 800.0;
            foreach (var line in lines)
            {
                page.AddText(line, 10, new PdfPoint(40, y), font);
                y -= 16;
            }
        }

        return builder.Build();
    }

    /// <summary>A PDF whose pages carry no text at all (stands for a scanned document).</summary>
    public static byte[] BlankPdf(int pages = 1)
    {
        var builder = new PdfDocumentBuilder();
        for (var i = 0; i < pages; i++)
        {
            builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        }

        return builder.Build();
    }

    /// <summary>Hand-written minimal PDF with one blank page, optionally with extra catalog entries (for active-content tests).</summary>
    public static byte[] RawPdf(string catalogExtra = "", string pageExtra = "", string trailerExtra = "")
    {
        var objects = new[]
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] {pageExtra} >>",
        };
        var sb = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xref = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (var o in offsets)
        {
            sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        }

        sb.Append($"trailer\n<< /Size 4 /Root 1 0 R {trailerExtra} >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ---- images -------------------------------------------------------------------------------------------------------------

    public static byte[] Png(int width, int height) => Encode(width, height, SKEncodedImageFormat.Png);

    public static byte[] Jpeg(int width, int height) => Encode(width, height, SKEncodedImageFormat.Jpeg);

    public static byte[] WebP(int width, int height) => Encode(width, height, SKEncodedImageFormat.Webp);

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black };
            using var font = new SKFont { Size = Math.Max(8, height / 6f) };
            canvas.DrawText("SYNTHETIC", 4, height / 2f, SKTextAlign.Left, font, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>A PNG that declares enormous dimensions in its header but has no pixel data (the bomb shape). Only the IHDR is read by the inspector.</summary>
    public static byte[] PngBomb(int width, int height)
    {
        var png = Png(8, 8);
        // IHDR data starts at offset 16: width(4) height(4), both big-endian.
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20, 4), height);
        return png;
    }

    public static byte[] Append(byte[] data, byte[] extra) => [.. data, .. extra];
}
