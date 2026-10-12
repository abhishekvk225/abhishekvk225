using Microsoft.Extensions.Options;

namespace NexaVerify.Infrastructure.Verification.Documents;

/// <summary>
/// Structural safety ceilings of the document pipeline (<c>Verification:DocumentPipeline:*</c>). These are not business limits (those are
/// <c>Verification:Upload:*</c>); they bound work done on hostile input. Validated at start-up.
/// </summary>
public sealed class DocumentPipelineOptions
{
    public const string SectionName = "Verification:DocumentPipeline";

    /// <summary>Longest side an image is scaled down to before OCR.</summary>
    public int OcrMaxImageSide { get; set; } = 4000;

    /// <summary>Indirect objects visited when scanning a PDF for active content.</summary>
    public int MaxScannedPdfObjects { get; set; } = 50_000;

    /// <summary>Nesting depth followed when scanning a PDF object.</summary>
    public int MaxPdfTokenDepth { get; set; } = 32;

    /// <summary>The PDF end-of-file marker must appear within this many bytes of the end.</summary>
    public int PdfEofWindowBytes { get; set; } = 2048;

    /// <summary>Blank bytes tolerated after the end marker of a PDF or image. Anything else is treated as appended (polyglot) data.</summary>
    public int TrailingBlankBytesTolerance { get; set; } = 1024;

    /// <summary>Output of one OCR process is cut off beyond this size.</summary>
    public int MaxOcrOutputBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>How long the Tesseract availability probe result is cached.</summary>
    public int OcrProbeCacheSeconds { get; set; } = 60;

    /// <summary>How long a Tesseract availability probe may run.</summary>
    public int OcrProbeTimeoutSeconds { get; set; } = 10;
}

public sealed class DocumentPipelineOptionsValidator : IValidateOptions<DocumentPipelineOptions>
{
    public ValidateOptionsResult Validate(string? name, DocumentPipelineOptions o)
    {
        var errors = new List<string>();
        void Positive(int v, string n)
        {
            if (v < 1)
            {
                errors.Add($"{DocumentPipelineOptions.SectionName}:{n} must be positive.");
            }
        }

        Positive(o.OcrMaxImageSide, nameof(o.OcrMaxImageSide));
        Positive(o.MaxScannedPdfObjects, nameof(o.MaxScannedPdfObjects));
        Positive(o.MaxPdfTokenDepth, nameof(o.MaxPdfTokenDepth));
        Positive(o.PdfEofWindowBytes, nameof(o.PdfEofWindowBytes));
        Positive(o.MaxOcrOutputBytes, nameof(o.MaxOcrOutputBytes));
        Positive(o.OcrProbeCacheSeconds, nameof(o.OcrProbeCacheSeconds));
        Positive(o.OcrProbeTimeoutSeconds, nameof(o.OcrProbeTimeoutSeconds));
        if (o.TrailingBlankBytesTolerance < 0)
        {
            errors.Add($"{DocumentPipelineOptions.SectionName}:TrailingBlankBytesTolerance must not be negative.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
