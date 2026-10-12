using NexaVerify.Application.Verification.Abstractions;

namespace NexaVerify.Infrastructure.Verification.Ocr;

/// <summary>Used when <c>Verification:Ocr:Provider</c> is <c>none</c>: scanned documents then fail with OCR_UNAVAILABLE.</summary>
public sealed class NullOcrProvider : IOcrProvider
{
    public string ProviderId => "none";

    public bool IsAvailable => false;

    public Task<OcrPage> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken) =>
        throw new OcrUnavailableException("OCR is disabled by configuration.");
}
