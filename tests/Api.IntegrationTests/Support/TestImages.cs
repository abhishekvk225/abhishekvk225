using SkiaSharp;

namespace NexaVerify.Api.IntegrationTests.Support;

/// <summary>Synthetic pictures for the mock face engine: a "person" is a seeded pattern; another photo of them is the same pattern plus noise.</summary>
public static class TestImages
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

        return Encode(bitmap, SKEncodedImageFormat.Jpeg);
    }

    /// <summary>Same picture as <see cref="Person"/> but as PNG (the type is decided from bytes, not the declared content type).</summary>
    public static byte[] PersonPng(int seed, int size = 200)
    {
        var jpeg = Person(seed, 0, size);
        using var bitmap = SKBitmap.Decode(jpeg);
        return Encode(bitmap, SKEncodedImageFormat.Png);
    }

    /// <summary>A uniform picture: the mock engine finds no face in it.</summary>
    public static byte[] Blank(int size = 200) => Solid(size, size, 128);

    /// <summary>Very wide picture: the mock engine reports two faces.</summary>
    public static byte[] TwoPeople()
    {
        using var bitmap = new SKBitmap(500, 150, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var levels = new byte[64];
        new Random(99).NextBytes(levels);
        for (var y = 0; y < 150; y++)
        {
            for (var x = 0; x < 500; x++)
            {
                var v = levels[(y * 8 / 150 * 8) + (x * 8 / 500)];
                bitmap.SetPixel(x, y, new SKColor(v, v, v));
            }
        }

        return Encode(bitmap, SKEncodedImageFormat.Jpeg);
    }

    /// <summary>A detectable but dull picture (low contrast): below the default quality threshold.</summary>
    public static byte[] Dull(int seed = 5)
    {
        var random = new Random(seed);
        var levels = Enumerable.Range(0, 64).Select(_ => (byte)random.Next(100, 161)).ToArray(); // std ≈ 17: detectable, but quality ≈ 0.3
        using var bitmap = new SKBitmap(200, 200, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < 200; y++)
        {
            for (var x = 0; x < 200; x++)
            {
                var v = levels[(y * 8 / 200 * 8) + (x * 8 / 200)];
                bitmap.SetPixel(x, y, new SKColor(v, v, v));
            }
        }

        return Encode(bitmap, SKEncodedImageFormat.Jpeg);
    }

    public static byte[] Solid(int width, int height, byte value)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(new SKColor(value, value, value));
        return Encode(bitmap, SKEncodedImageFormat.Jpeg);
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format)
    {
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, 95).ToArray();
    }
}
