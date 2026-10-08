using SkiaSharp;

namespace NexaVerify.Load;

/// <summary>
/// Synthetic pictures for the mock face engine: a "person" is a seeded 8x8 pattern, another photo of the same person is the same
/// pattern plus noise. Real providers need real photos; point the k6 scripts at your own fixtures for those.
/// </summary>
public static class FaceImages
{
    public static byte[] Person(int seed, int variation = 0, int size = 200)
    {
        var pattern = new Random(seed);
        var levels = new byte[8 * 8];
        pattern.NextBytes(levels);
        var noise = new Random((seed * 7919) + variation + 1);
        using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var v = levels[(y * 8 / size * 8) + (x * 8 / size)];
                if (variation != 0)
                {
                    v = (byte)Math.Clamp(v + noise.Next(-6, 7), 0, 255);
                }

                bitmap.SetPixel(x, y, new SKColor(v, v, v));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 95).ToArray();
    }
}
