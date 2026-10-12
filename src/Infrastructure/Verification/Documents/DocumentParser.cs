using Microsoft.Extensions.Logging;
using NexaVerify.Application.Common;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Parsing;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Infrastructure.Verification.Documents;

/// <summary>
/// Bytes to text lines. PDFs: digital text first; a page with fewer characters than <c>MinTextCharsPerPage</c> is treated as scanned,
/// rasterised and OCR-ed. Images: OCR. If OCR is needed but unavailable the result is a failure (<c>OCR_UNAVAILABLE</c>), never empty
/// text. Only lines and confidences leave this class; page images exist in memory only.
/// </summary>
public sealed class DocumentParser : IDocumentParser
{
    private readonly IPdfTextExtractor _text;
    private readonly IPdfRasterizer _rasterizer;
    private readonly IDocumentImagePreprocessor _preprocessor;
    private readonly IOcrProvider _ocr;
    private readonly ILogger<DocumentParser> _logger;

    public DocumentParser(
        IPdfTextExtractor text, IPdfRasterizer rasterizer, IDocumentImagePreprocessor preprocessor, IOcrProvider ocr, ILogger<DocumentParser> logger)
    {
        _text = text;
        _rasterizer = rasterizer;
        _preprocessor = preprocessor;
        _ocr = ocr;
        _logger = logger;
    }

    public async Task<Result<DocumentText>> ParseAsync(ReadOnlyMemory<byte> content, DocumentKind kind, ParseOptions options, CancellationToken cancellationToken)
    {
        if (content.IsEmpty)
        {
            return Unreadable();
        }

        try
        {
            return kind == DocumentKind.Pdf
                ? await ParsePdfAsync(content, options, cancellationToken).ConfigureAwait(false)
                : await ParseImageAsync(content, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OcrUnavailableException)
        {
            return OcrUnavailable();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogInformation("Document parsing failed ({ExceptionType})", ex.GetType().Name);
            return Unreadable();
        }
    }

    private async Task<Result<DocumentText>> ParsePdfAsync(ReadOnlyMemory<byte> pdf, ParseOptions options, CancellationToken ct)
    {
        var digital = await _text.ExtractAsync(pdf, options.MaxPages, ct).ConfigureAwait(false);
        if (digital.Count == 0)
        {
            return Unreadable();
        }

        var pages = new List<PageText>(digital.Count);
        var ocrConfidences = new List<decimal>();
        var usedDigital = false;
        var usedOcr = false;
        foreach (var page in digital)
        {
            var chars = page.Lines.Sum(l => l.Text.Count(c => !char.IsWhiteSpace(c)));
            if (chars >= options.MinTextCharsPerPage)
            {
                pages.Add(page);
                usedDigital = true;
                continue;
            }

            if (!_ocr.IsAvailable)
            {
                return OcrUnavailable();
            }

            var png = _rasterizer is IPdfPageRasterizer single
                ? await single.RenderPageAsync(pdf, page.Number, options.Dpi, ct).ConfigureAwait(false)
                : (await _rasterizer.RenderPagesAsync(pdf, page.Number, options.Dpi, ct).ConfigureAwait(false))[page.Number - 1];
            var recognised = await RecogniseAsync(png, options, page.Number, ct).ConfigureAwait(false);
            if (recognised.IsFailure)
            {
                return Result<DocumentText>.Failure(recognised.Error!);
            }

            pages.Add(new PageText(page.Number, recognised.Value.Lines));
            if (recognised.Value.MeanConfidence is { } c)
            {
                ocrConfidences.Add(c);
            }

            usedOcr = true;
        }

        return Finish(pages, usedOcr ? (usedDigital ? TextSource.Mixed : TextSource.Ocr) : TextSource.DigitalPdf, ocrConfidences);
    }

    private async Task<Result<DocumentText>> ParseImageAsync(ReadOnlyMemory<byte> image, ParseOptions options, CancellationToken ct)
    {
        if (!_ocr.IsAvailable)
        {
            return OcrUnavailable();
        }

        var prepared = await _preprocessor.PrepareForOcrAsync(image, ct).ConfigureAwait(false);
        if (prepared.IsFailure)
        {
            return Result<DocumentText>.Failure(prepared.Error!);
        }

        var page = await _ocr.RecognizeAsync(new OcrRequest(prepared.Value, options.Language, 1), ct).ConfigureAwait(false);
        return Finish([new PageText(1, page.Lines)], TextSource.Ocr, page.MeanConfidence is { } c ? [c] : []);
    }

    private async Task<Result<OcrPage>> RecogniseAsync(byte[] png, ParseOptions options, int pageNumber, CancellationToken ct)
    {
        var prepared = await _preprocessor.PrepareForOcrAsync(png, ct).ConfigureAwait(false);
        if (prepared.IsFailure)
        {
            return Result<OcrPage>.Failure(prepared.Error!);
        }

        return await _ocr.RecognizeAsync(new OcrRequest(prepared.Value, options.Language, pageNumber), ct).ConfigureAwait(false);
    }

    private static Result<DocumentText> Finish(List<PageText> pages, TextSource source, List<decimal> confidences)
    {
        if (pages.All(p => p.Lines.Count == 0 || p.Lines.All(l => string.IsNullOrWhiteSpace(l.Text))))
        {
            return Unreadable();
        }

        return new DocumentText(pages, source, confidences.Count == 0 ? null : Math.Round(confidences.Average(), 4));
    }

    private static Error UnreadableError() => new(ErrorCodes.DocumentUnreadable, "No readable text was found in the document.", ErrorType.UnprocessableEntity);

    private static Result<DocumentText> Unreadable() => UnreadableError();

    private static Result<DocumentText> OcrUnavailable() =>
        new Error(ErrorCodes.OcrUnavailable, "Text recognition is unavailable for this scanned document.", ErrorType.Unavailable);
}
