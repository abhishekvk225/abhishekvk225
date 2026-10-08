using NexaVerify.Application.Faces;
using SkiaSharp;

namespace NexaVerify.Infrastructure.Faces;

/// <summary>
/// Deterministic stand-in for a real recognition model, for tests, demos and local development. It does NOT recognise faces: the
/// "embedding" is a normalised 16×16 brightness thumbnail, so the same picture (or a lightly altered copy) scores near 1 and
/// unrelated pictures score near 0. Conventions: a nearly flat image has no face; an image wider than 2.2× its height has two.
/// </summary>
public sealed class MockFaceEngine : IFaceEngine
{
    private const int Grid = 16;

    public string Provider => "mock";

    public string ModelVersion => "mock-1";

    public int Dimensions => Grid * Grid;

    public Task<IReadOnlyList<DetectedFace>> DetectAsync(PreparedImage image, CancellationToken cancellationToken)
    {
        using var bitmap = Decode(image);
        var (_, std) = Stats(Thumbnail(bitmap, 32));
        var quality = (decimal)(Math.Min(1.0, std / 60.0) * Math.Min(1.0, Math.Min(image.Width, image.Height) / 200.0));
        quality = Math.Round(quality, 4);

        IReadOnlyList<DetectedFace> faces;
        if (std < 12)
        {
            faces = [];
        }
        else if ((double)image.Width / image.Height > 2.2)
        {
            faces =
            [
                new DetectedFace(0, 0, image.Width / 2, image.Height, quality),
                new DetectedFace(image.Width / 2, 0, image.Width / 2, image.Height, quality),
            ];
        }
        else
        {
            faces = [new DetectedFace(0, 0, image.Width, image.Height, quality)];
        }

        return Task.FromResult(faces);
    }

    public Task<float[]> ExtractAsync(PreparedImage image, DetectedFace face, CancellationToken cancellationToken)
    {
        using var bitmap = Decode(image);
        var pixels = Thumbnail(bitmap, Grid);
        var (mean, _) = Stats(pixels);
        var vector = pixels.Select(p => (float)(p - mean)).ToArray();
        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        if (norm > 1e-6)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = (float)(vector[i] / norm);
            }
        }

        return Task.FromResult(vector);
    }

    public double Similarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na < 1e-12 || nb < 1e-12 ? 0 : Math.Clamp(dot / Math.Sqrt(na * nb), 0, 1);
    }

    private static SKBitmap Decode(PreparedImage image) =>
        SKBitmap.Decode(image.Jpeg) ?? throw new FaceProviderException("The prepared image could not be decoded.");

    private static double[] Thumbnail(SKBitmap source, int size)
    {
        using var small = source.Resize(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new FaceProviderException("The image could not be resized.");
        var result = new double[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var c = small.GetPixel(x, y);
                result[(y * size) + x] = (0.299 * c.Red) + (0.587 * c.Green) + (0.114 * c.Blue);
            }
        }

        return result;
    }

    private static (double Mean, double Std) Stats(double[] values)
    {
        var mean = values.Average();
        return (mean, Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Length));
    }
}
