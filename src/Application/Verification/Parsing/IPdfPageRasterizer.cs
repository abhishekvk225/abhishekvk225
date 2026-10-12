namespace NexaVerify.Application.Verification.Parsing;

/// <summary>
/// Optional capability of an <see cref="Abstractions.IPdfRasterizer"/>: render a single page, so a mixed digital/scanned PDF does not
/// rasterise every page. The document parser falls back to <c>RenderPagesAsync</c> when the rasteriser does not implement it.
/// </summary>
public interface IPdfPageRasterizer : Abstractions.IPdfRasterizer
{
    /// <summary>One PNG for the 1-based <paramref name="pageNumber"/>.</summary>
    Task<byte[]> RenderPageAsync(ReadOnlyMemory<byte> pdf, int pageNumber, int dpi, CancellationToken cancellationToken);
}
