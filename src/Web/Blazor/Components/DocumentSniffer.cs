namespace NexaVerify.Web.Components;

/// <summary>
/// Cheap client-side document checks (magic bytes, name versus content, a rough PDF page count) so people hear about a wrong file before
/// it is uploaded. The API re-checks everything on the server and stays the only authority.
/// </summary>
public static class DocumentSniffer
{
    public const string Pdf = "application/pdf";
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string WebP = "image/webp";

    /// <summary>Extensions the API accepts by default; the catalogue's limits replace this when they arrive.</summary>
    public static IReadOnlyList<string> DefaultExtensions { get; } = [".pdf", ".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>The real type of the file by its first bytes, or null when it is none of the accepted ones.</summary>
    public static string? Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 5 && head[..5].SequenceEqual("%PDF-"u8))
        {
            return Pdf;
        }

        return ImageSniffer.Detect(head);
    }

    public static string ExtensionFor(string contentType) => contentType switch
    {
        Pdf => ".pdf",
        Png => ".png",
        WebP => ".webp",
        _ => ".jpg",
    };

    /// <summary>"PDF" / "JPEG" / "PNG" / "WebP".</summary>
    public static string KindLabel(string? contentType) => contentType switch
    {
        Pdf => "PDF",
        Jpeg => "JPEG",
        Png => "PNG",
        WebP => "WebP",
        _ => "Unknown",
    };

    public static string ExtensionOf(string? fileName)
    {
        var dot = fileName?.LastIndexOf('.') ?? -1;
        return dot < 0 || fileName is null ? string.Empty : fileName[dot..].ToLowerInvariant();
    }

    /// <summary>True when the file name's extension is one the real type goes by (".jpg" and ".jpeg" for JPEG).</summary>
    public static bool ExtensionMatches(string? fileName, string contentType)
    {
        var extension = ExtensionOf(fileName);
        return contentType switch
        {
            Pdf => extension == ".pdf",
            Jpeg => extension is ".jpg" or ".jpeg",
            Png => extension == ".png",
            WebP => extension == ".webp",
            _ => false,
        };
    }

    /// <summary>
    /// A rough page count for a PDF: the number of page objects. Pages packed into compressed object streams are not seen, so this is a
    /// hint only (null = could not tell). The server counts for real.
    /// </summary>
    public static int? EstimatePdfPages(ReadOnlySpan<byte> pdf)
    {
        var count = 0;
        var type = "/Type"u8;
        var page = "/Page"u8;
        var offset = 0;
        while (offset < pdf.Length)
        {
            var found = pdf[offset..].IndexOf(type);
            if (found < 0)
            {
                break;
            }

            var cursor = offset + found + type.Length;
            while (cursor < pdf.Length && pdf[cursor] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')
            {
                cursor++;
            }

            if (cursor + page.Length <= pdf.Length && pdf.Slice(cursor, page.Length).SequenceEqual(page)
                && (cursor + page.Length == pdf.Length || pdf[cursor + page.Length] != (byte)'s'))
            {
                count++;
            }

            offset += found + type.Length;
        }

        return count == 0 ? null : count;
    }

    public static string SizeText(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.#} MB" : $"{Math.Max(1, bytes / 1024):N0} KB";
}
