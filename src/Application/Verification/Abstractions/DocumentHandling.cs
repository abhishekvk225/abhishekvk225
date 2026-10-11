using NexaVerify.Application.Common;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Abstractions;

/// <summary>The controller reads the form file into a bounded buffer. <c>FileName</c> is never used for storage paths.</summary>
public sealed record DocumentUpload(string FileName, string? DeclaredContentType, byte[] Content)
{
    public override string ToString() => nameof(DocumentUpload);
}

/// <summary>Per-module limits applied at accept time (built from <c>Verification:Upload:*</c>).</summary>
public sealed record UploadPolicy(
    long MaxFileBytes, IReadOnlyList<string> AllowedExtensions, int MaxPages, int MaxImagePixels, bool RejectActiveContent);

public sealed record DocumentInspection(
    DocumentKind Kind, string ContentType, string Extension, int PageCount, int? Width, int? Height, bool IsEncrypted, bool HasActiveContent, byte[] Sha256);

/// <summary>Cheap and bounded; runs at accept time (timeout and bulkhead semaphore). Nothing is stored or charged before it succeeds.</summary>
public interface IDocumentInspector
{
    Task<Result<DocumentInspection>> InspectAsync(DocumentUpload upload, UploadPolicy policy, CancellationToken cancellationToken);
}

public sealed record ParseOptions(int MaxPages, string Language, int Dpi, int MinTextCharsPerPage);

public sealed record TextLine(string Text, decimal? Confidence, int? X, int? Y, int? Width, int? Height)
{
    public override string ToString() => nameof(TextLine);
}

public sealed record PageText(int Number, IReadOnlyList<TextLine> Lines)
{
    public override string ToString() => nameof(PageText);
}

public sealed record DocumentText(IReadOnlyList<PageText> Pages, TextSource Source, decimal? MeanConfidence)
{
    public override string ToString() => nameof(DocumentText);
}

/// <summary>Bytes to text lines: digital text first, OCR when text is missing or poor. Fails with OCR_UNAVAILABLE rather than returning empty text silently.</summary>
public interface IDocumentParser
{
    Task<Result<DocumentText>> ParseAsync(ReadOnlyMemory<byte> content, DocumentKind kind, ParseOptions options, CancellationToken cancellationToken);
}

public interface IPdfTextExtractor
{
    Task<IReadOnlyList<PageText>> ExtractAsync(ReadOnlyMemory<byte> pdf, int maxPages, CancellationToken cancellationToken);
}

public interface IPdfRasterizer
{
    /// <summary>One PNG per page.</summary>
    Task<IReadOnlyList<byte[]>> RenderPagesAsync(ReadOnlyMemory<byte> pdf, int maxPages, int dpi, CancellationToken cancellationToken);
}

public interface IDocumentImagePreprocessor
{
    /// <summary>Decode caps, EXIF orientation, grayscale, bounded size.</summary>
    Task<Result<byte[]>> PrepareForOcrAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken);
}

public sealed record OcrRequest(byte[] ImagePng, string Language, int PageNumber)
{
    public override string ToString() => nameof(OcrRequest);
}

public sealed record OcrPage(int PageNumber, IReadOnlyList<TextLine> Lines, decimal? MeanConfidence);

/// <summary>Thrown by an <see cref="IOcrProvider"/> for infrastructure faults only (binary missing, timeout, crash).</summary>
public sealed class OcrUnavailableException : Exception
{
    public OcrUnavailableException()
        : base("OCR is unavailable.")
    {
    }

    public OcrUnavailableException(string message)
        : base(message)
    {
    }

    public OcrUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IOcrProvider
{
    /// <summary>For example <c>tesseract-cli</c> or <c>none</c>.</summary>
    string ProviderId { get; }

    /// <summary>Binary and language data present (health, admin status).</summary>
    bool IsAvailable { get; }

    Task<OcrPage> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken);
}

public sealed record StoredDocument(Guid DocumentId, Guid RequestId, int SizeBytes, byte[] Sha256);

/// <summary>Encrypted document storage (ADR-19). No public URLs; the tenant filter applies to every read.</summary>
public interface IDocumentStorageService
{
    /// <summary>Encrypts with <c>IClientEncryption</c>, purpose <c>verification-document:{requestId:N}</c>.</summary>
    Task<StoredDocument> StoreAsync(Guid clientId, Guid requestId, DocumentUpload upload, DocumentInspection inspection, CancellationToken cancellationToken);

    /// <summary>Tenant filter applies; decrypts.</summary>
    Task<Result<byte[]>> ReadAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>Hard-deletes the content and flags the metadata purged.</summary>
    Task DeleteAsync(Guid documentId, CancellationToken cancellationToken);
}
