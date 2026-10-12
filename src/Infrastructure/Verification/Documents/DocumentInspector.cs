using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Common;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Verification;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace NexaVerify.Infrastructure.Verification.Documents;

/// <summary>
/// The accept-time gate for uploaded documents. Four independent checks must agree: file extension, declared content type, magic bytes
/// and the structure itself. It also rejects appended payloads (polyglots), encrypted PDFs, PDF active content (JavaScript, launch,
/// embedded files, XFA, open actions, rich media), too many pages and images with too many pixels (decompression bomb) BEFORE any
/// decode. Bounded by a timeout and a concurrency gate. Nothing it does touches the network or the file system.
/// </summary>
public sealed class DocumentInspector : IDocumentInspector, IDisposable
{
    private static readonly HashSet<string> ActiveKeys = new(StringComparer.Ordinal)
    {
        "JS", "JavaScript", "Launch", "EmbeddedFile", "EmbeddedFiles", "XFA", "OpenAction", "AA", "RichMedia", "ImportData", "SubmitForm",
        "GoToR", "GoToE",
    };

    private readonly VerificationOptions _options;
    private readonly DocumentPipelineOptions _pipeline;
    private readonly ILogger<DocumentInspector> _logger;
    private readonly SemaphoreSlim _gate;

    public DocumentInspector(IOptions<VerificationOptions> options, IOptions<DocumentPipelineOptions> pipeline, ILogger<DocumentInspector> logger)
    {
        _options = options.Value;
        _pipeline = pipeline.Value;
        _logger = logger;
        _gate = new SemaphoreSlim(Math.Max(1, _options.Upload.InspectMaxConcurrent));
    }

    public void Dispose() => _gate.Dispose();

    public async Task<Result<DocumentInspection>> InspectAsync(DocumentUpload upload, UploadPolicy policy, CancellationToken cancellationToken)
    {
        var content = upload.Content;
        if (content is null || content.Length == 0)
        {
            return Fail(ErrorCodes.DocumentRequired, "The document is empty.", ErrorType.Validation);
        }

        if (content.Length > policy.MaxFileBytes)
        {
            return Fail(ErrorCodes.DocumentTooLarge, "The document is larger than the allowed size.", ErrorType.PayloadTooLarge);
        }

        // The name is used for its extension only, never as a path.
        var extension = SafeExtension(upload.FileName);
        if (extension is null || !policy.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return Fail(ErrorCodes.DocumentUnsupportedType, "This file type is not accepted.", ErrorType.Validation);
        }

        if (Sniff(content) is not { } kind)
        {
            return Fail(ErrorCodes.DocumentUnsupportedType, "The file content is not a supported PDF or image.", ErrorType.Validation);
        }

        if (!ExtensionMatches(kind, extension) || !ContentTypeMatches(kind, upload.DeclaredContentType))
        {
            return Fail(ErrorCodes.DocumentInvalid, "The file extension, content type and content do not agree.", ErrorType.Validation);
        }

        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(_options.Upload.InspectTimeoutSeconds), cancellationToken).ConfigureAwait(false))
        {
            return Result<DocumentInspection>.Failure(Error.Unavailable(ErrorCodes.ProcessingUnavailable, "The server is busy inspecting documents. Please retry shortly."));
        }

        try
        {
            var work = Task.Run(() => Inspect(content, kind, extension, policy), CancellationToken.None);
            return await work.WaitAsync(TimeSpan.FromSeconds(_options.Upload.InspectTimeoutSeconds), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Fail(ErrorCodes.DocumentInvalid, "The document could not be inspected in time.", ErrorType.Validation);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Decides the kind from the first bytes only. A PDF header must be at offset 0 (a shifted header is a polyglot trick).</summary>
    public static DocumentKind? Sniff(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 5 && d[0] == '%' && d[1] == 'P' && d[2] == 'D' && d[3] == 'F' && d[4] == '-')
        {
            return DocumentKind.Pdf;
        }

        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF)
        {
            return DocumentKind.Jpeg;
        }

        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A)
        {
            return DocumentKind.Png;
        }

        if (d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P')
        {
            return DocumentKind.WebP;
        }

        return null;
    }

    public static string ContentTypeOf(DocumentKind kind) => kind switch
    {
        DocumentKind.Pdf => "application/pdf",
        DocumentKind.Jpeg => "image/jpeg",
        DocumentKind.Png => "image/png",
        _ => "image/webp",
    };

    private static string? SafeExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot < 0 || dot == name.Length - 1 ? null : name[dot..].ToLowerInvariant();
    }

    private static bool ExtensionMatches(DocumentKind kind, string ext) => kind switch
    {
        DocumentKind.Pdf => ext == ".pdf",
        DocumentKind.Jpeg => ext is ".jpg" or ".jpeg",
        DocumentKind.Png => ext == ".png",
        _ => ext == ".webp",
    };

    private static bool ContentTypeMatches(DocumentKind kind, string? declared)
    {
        if (string.IsNullOrWhiteSpace(declared))
        {
            return true;
        }

        var type = declared.Split(';')[0].Trim().ToLowerInvariant();
        return type == "application/octet-stream" || type == ContentTypeOf(kind) || (kind == DocumentKind.Jpeg && type == "image/jpg");
    }

    private static Result<DocumentInspection> Fail(string code, string message, ErrorType type) =>
        Result<DocumentInspection>.Failure(new Error(code, message, type));

    private Result<DocumentInspection> Inspect(byte[] content, DocumentKind kind, string extension, UploadPolicy policy)
    {
        try
        {
            var sha = SHA256.HashData(content);
            if (kind == DocumentKind.Pdf)
            {
                return InspectPdf(content, extension, policy, sha);
            }

            if (!NoAppendedData(content, kind))
            {
                return Fail(ErrorCodes.DocumentInvalid, "The image contains unexpected data after its end.", ErrorType.Validation);
            }

            using var data = SKData.CreateCopy(content);
            using var codec = SKCodec.Create(data);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            {
                return Fail(ErrorCodes.DocumentInvalid, "The image could not be read.", ErrorType.Validation);
            }

            var pixels = (long)codec.Info.Width * codec.Info.Height;
            if (pixels > policy.MaxImagePixels)
            {
                return Fail(ErrorCodes.DocumentTooLarge, "The image has more pixels than allowed.", ErrorType.PayloadTooLarge);
            }

            return new DocumentInspection(kind, ContentTypeOf(kind), extension, 1, codec.Info.Width, codec.Info.Height, false, false, sha);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogInformation("Document inspection failed ({ExceptionType})", ex.GetType().Name);
            return Fail(ErrorCodes.DocumentInvalid, "The document could not be read.", ErrorType.Validation);
        }
    }

    private Result<DocumentInspection> InspectPdf(byte[] content, string extension, UploadPolicy policy, byte[] sha)
    {
        if (!PdfEndsCleanly(content))
        {
            return Fail(ErrorCodes.DocumentInvalid, "The PDF has unexpected data after its end marker.", ErrorType.Validation);
        }

        var encryptedMarker = Contains(content, "/Encrypt");
        try
        {
            using var doc = PdfDocument.Open(content, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            if (doc.IsEncrypted || encryptedMarker)
            {
                return Fail(ErrorCodes.DocumentEncrypted, "Encrypted or password-protected PDFs are not accepted.", ErrorType.Validation);
            }

            if (doc.NumberOfPages < 1)
            {
                return Fail(ErrorCodes.DocumentInvalid, "The PDF has no pages.", ErrorType.Validation);
            }

            if (doc.NumberOfPages > policy.MaxPages)
            {
                return Fail(ErrorCodes.DocumentTooManyPages, "The PDF has more pages than allowed.", ErrorType.Validation);
            }

            var active = HasActiveContent(doc, content);
            if (active && policy.RejectActiveContent)
            {
                return Fail(ErrorCodes.DocumentActiveContent, "PDFs with scripts, launch actions or embedded files are not accepted.", ErrorType.Validation);
            }

            return new DocumentInspection(DocumentKind.Pdf, ContentTypeOf(DocumentKind.Pdf), extension, doc.NumberOfPages, null, null, false, active, sha);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (encryptedMarker || ex.GetType().Name.Contains("Encrypt", StringComparison.OrdinalIgnoreCase))
            {
                return Fail(ErrorCodes.DocumentEncrypted, "Encrypted or password-protected PDFs are not accepted.", ErrorType.Validation);
            }

            _logger.LogInformation("PDF inspection failed ({ExceptionType})", ex.GetType().Name);
            return Fail(ErrorCodes.DocumentInvalid, "The PDF could not be read.", ErrorType.Validation);
        }
    }

    private bool HasActiveContent(PdfDocument doc, byte[] raw)
    {
        // 1. Object graph: catches names inside compressed object streams and hex-escaped names, which a byte scan misses.
        var visited = 0;
        try
        {
            foreach (var reference in doc.Structure.CrossReferenceTable.ObjectOffsets.Keys)
            {
                if (++visited > _pipeline.MaxScannedPdfObjects)
                {
                    return true; // too complex to vouch for: treat as unsafe
                }

                var obj = doc.Structure.GetObject(reference);
                if (obj?.Data is { } token && Walk(token, 0))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true; // an object we cannot walk cannot be vouched for
        }

        // 2. Conservative byte scan of the raw file (uncompressed bodies and anything the graph walk could not reach).
        return ActiveKeys.Any(k => k is not ("AA" or "JS") && Contains(raw, "/" + k)) || ContainsToken(raw, "/JS") || ContainsToken(raw, "/AA");
    }

    private bool Walk(IToken token, int depth)
    {
        if (depth > _pipeline.MaxPdfTokenDepth)
        {
            return true;
        }

        switch (token)
        {
            case DictionaryToken d:
                foreach (var (key, value) in d.Data)
                {
                    if (ActiveKeys.Contains(key) || (value is NameToken n && ActiveKeys.Contains(n.Data)) || Walk(value, depth + 1))
                    {
                        return true;
                    }
                }

                return false;
            case StreamToken s:
                return Walk(s.StreamDictionary, depth + 1);
            case ArrayToken a:
                return a.Data.Any(t => Walk(t, depth + 1));
            case ObjectToken o:
                return Walk(o.Data, depth + 1);
            default:
                return false;
        }
    }

    private static bool Contains(byte[] data, string ascii) => data.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(ascii)) >= 0;

    /// <summary>Matches <c>/JS</c> or <c>/AA</c> only when the name ends there (so /JSON-like names do not trigger).</summary>
    private static bool ContainsToken(byte[] data, string name)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(name);
        var span = data.AsSpan();
        var start = 0;
        while (true)
        {
            var i = span[start..].IndexOf(needle);
            if (i < 0)
            {
                return false;
            }

            var end = start + i + needle.Length;
            if (end >= span.Length || !IsNameChar(span[end]))
            {
                return true;
            }

            start += i + 1;
        }
    }

    private static bool IsNameChar(byte b) => (b >= '0' && b <= '9') || (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || b == '#' || b == '_' || b == '-' || b == '.';

    private bool PdfEndsCleanly(byte[] d)
    {
        var window = Math.Min(_pipeline.PdfEofWindowBytes + _pipeline.TrailingBlankBytesTolerance, d.Length);
        var tail = d.AsSpan(d.Length - window);
        var eof = tail.LastIndexOf("%%EOF"u8);
        if (eof < 0)
        {
            return false;
        }

        var after = tail[(eof + 5)..];
        return after.Length <= _pipeline.TrailingBlankBytesTolerance && IsBlank(after);
    }

    private static bool IsBlank(ReadOnlySpan<byte> s)
    {
        foreach (var b in s)
        {
            if (b is not (0 or 9 or 10 or 12 or 13 or 32))
            {
                return false;
            }
        }

        return true;
    }

    private bool NoAppendedData(byte[] d, DocumentKind kind)
    {
        var tolerance = _pipeline.TrailingBlankBytesTolerance;
        switch (kind)
        {
            case DocumentKind.Jpeg:
            {
                var i = d.AsSpan().LastIndexOf(new byte[] { 0xFF, 0xD9 });
                return i >= 0 && d.Length - (i + 2) <= tolerance && IsBlank(d.AsSpan(i + 2));
            }

            case DocumentKind.Png:
            {
                var pos = 8;
                while (pos + 12 <= d.Length)
                {
                    long length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos, 4));
                    var isEnd = d[pos + 4] == 'I' && d[pos + 5] == 'E' && d[pos + 6] == 'N' && d[pos + 7] == 'D';
                    var next = pos + 12 + length;
                    if (next > d.Length)
                    {
                        return false;
                    }

                    if (isEnd)
                    {
                        return d.Length - next <= tolerance && IsBlank(d.AsSpan((int)next));
                    }

                    pos = (int)next;
                }

                return false;
            }

            default: // WebP: the RIFF size field must describe the whole file (plus the optional pad byte)
            {
                long size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4, 4)) + 8;
                return d.Length >= size && d.Length - size <= tolerance + 1 && IsBlank(d.AsSpan((int)size));
            }
        }
    }
}
