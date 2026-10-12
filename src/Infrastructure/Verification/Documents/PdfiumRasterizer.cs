using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Parsing;
using NexaVerify.Application.Verification.Processing;
using PDFtoImage;
using SkiaSharp;

namespace NexaVerify.Infrastructure.Verification.Documents;

/// <summary>
/// Rasterises PDF pages with PDFium (PDFtoImage). The page size is read first and the pixel count at the requested DPI is checked
/// against <c>Upload:MaxImagePixels</c> BEFORE rendering (a huge MediaBox must not allocate a huge bitmap). Annotations and form fill
/// are off, so no PDF action is ever executed. PDFium is serialised inside PDFtoImage; concurrency is additionally capped here.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("macos")]
public sealed class PdfiumRasterizer : IPdfPageRasterizer, IDisposable
{
    private const double PointsPerInch = 72.0;

    private readonly VerificationOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PdfiumRasterizer(IOptions<VerificationOptions> options)
    {
        _options = options.Value;
    }

    public void Dispose() => _gate.Dispose();

    public async Task<IReadOnlyList<byte[]>> RenderPagesAsync(ReadOnlyMemory<byte> pdf, int maxPages, int dpi, CancellationToken cancellationToken)
    {
        var bytes = pdf.ToArray();
        var count = await RunAsync(() => PageCount(bytes), cancellationToken).ConfigureAwait(false);
        var pages = new List<byte[]>();
        for (var n = 1; n <= Math.Min(count, maxPages); n++)
        {
            pages.Add(await RenderPageAsync(pdf, n, dpi, cancellationToken).ConfigureAwait(false));
        }

        return pages;
    }

    public Task<byte[]> RenderPageAsync(ReadOnlyMemory<byte> pdf, int pageNumber, int dpi, CancellationToken cancellationToken)
    {
        var bytes = pdf.ToArray();
        return RunAsync(() => Render(bytes, pageNumber, dpi), cancellationToken);
    }

    private static int PageCount(byte[] pdf)
    {
        return Conversion.GetPageCount(pdf);
    }

    private byte[] Render(byte[] pdf, int pageNumber, int dpi)
    {
        var size = Conversion.GetPageSize(pdf, pageNumber - 1);
        var pixels = (double)size.Width * dpi / PointsPerInch * ((double)size.Height * dpi / PointsPerInch);
        if (pixels > _options.Upload.MaxImagePixels || size.Width <= 0 || size.Height <= 0)
        {
            throw new InvalidOperationException("The PDF page is too large to rasterise.");
        }

        using var bitmap = Conversion.ToImage(
            pdf, pageNumber - 1, options: new RenderOptions(Dpi: dpi, WithAnnotations: false, WithFormFill: false, BackgroundColor: SKColors.White));
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private async Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(work, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Registered where PDFium has no native build; scanned PDFs then fail with OCR_UNAVAILABLE instead of crashing.</summary>
public sealed class UnavailablePdfRasterizer : NexaVerify.Application.Verification.Abstractions.IPdfRasterizer
{
    public Task<IReadOnlyList<byte[]>> RenderPagesAsync(ReadOnlyMemory<byte> pdf, int maxPages, int dpi, CancellationToken cancellationToken) =>
        throw new NexaVerify.Application.Verification.Abstractions.OcrUnavailableException("PDF rasterisation is not available on this platform.");
}
