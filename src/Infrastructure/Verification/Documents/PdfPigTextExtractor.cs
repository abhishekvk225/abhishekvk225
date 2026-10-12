using NexaVerify.Application.Verification.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace NexaVerify.Infrastructure.Verification.Documents;

/// <summary>Digital text of a PDF (PdfPig, pure managed). Words are grouped into lines by baseline; a wide gap becomes a double space so column layouts survive.</summary>
public sealed class PdfPigTextExtractor : IPdfTextExtractor
{
    private const double SameLineFraction = 0.5;
    private const double ColumnGapFactor = 1.5;

    public Task<IReadOnlyList<PageText>> ExtractAsync(ReadOnlyMemory<byte> pdf, int maxPages, CancellationToken cancellationToken)
    {
        var bytes = pdf.ToArray();
        return Task.Run<IReadOnlyList<PageText>>(() => Extract(bytes, maxPages, cancellationToken), cancellationToken);
    }

    private static List<PageText> Extract(byte[] bytes, int maxPages, CancellationToken ct)
    {
        using var doc = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        var pages = new List<PageText>();
        var count = Math.Min(doc.NumberOfPages, maxPages);
        for (var n = 1; n <= count; n++)
        {
            ct.ThrowIfCancellationRequested();
            pages.Add(new PageText(n, Lines(doc.GetPage(n))));
        }

        return pages;
    }

    private static List<TextLine> Lines(Page page)
    {
        var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        var lines = new List<List<Word>>();
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom))
        {
            var home = lines.FirstOrDefault(l =>
            {
                var h = Math.Max(1.0, Math.Min(l[0].BoundingBox.Height, word.BoundingBox.Height));
                return Math.Abs(l[0].BoundingBox.Bottom - word.BoundingBox.Bottom) <= h * SameLineFraction;
            });
            if (home is null)
            {
                lines.Add([word]);
            }
            else
            {
                home.Add(word);
            }
        }

        var result = new List<TextLine>(lines.Count);
        foreach (var line in lines.OrderByDescending(l => l[0].BoundingBox.Bottom))
        {
            var ordered = line.OrderBy(w => w.BoundingBox.Left).ToList();
            var sb = new System.Text.StringBuilder();
            Word? prev = null;
            foreach (var w in ordered)
            {
                if (prev is not null)
                {
                    var gap = w.BoundingBox.Left - prev.BoundingBox.Right;
                    sb.Append(gap > Math.Max(1.0, w.BoundingBox.Height) * ColumnGapFactor ? "  " : " ");
                }

                sb.Append(w.Text);
                prev = w;
            }

            var left = ordered.Min(w => w.BoundingBox.Left);
            var right = ordered.Max(w => w.BoundingBox.Right);
            var bottom = ordered.Min(w => w.BoundingBox.Bottom);
            var top = ordered.Max(w => w.BoundingBox.Top);
            result.Add(new TextLine(sb.ToString(), null, (int)left, (int)(page.Height - top), (int)(right - left), (int)(top - bottom)));
        }

        return result;
    }
}
