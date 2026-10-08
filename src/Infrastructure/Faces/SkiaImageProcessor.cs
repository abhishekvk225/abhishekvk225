using System.Security.Cryptography;
using NexaVerify.Application.Common;
using NexaVerify.Application.Faces;
using NexaVerify.Contracts.Common;
using SkiaSharp;

namespace NexaVerify.Infrastructure.Faces;

/// <summary>
/// Hardens the one place untrusted binary input is parsed: the type is decided from magic bytes (never the client's file name or
/// content type), the size and pixel count are bounded BEFORE decoding (decompression-bomb guard), EXIF orientation is applied,
/// and the image is re-encoded as a plain JPEG so no metadata or polyglot payload survives. Concurrency is capped because a
/// large decode is memory-hungry.
/// </summary>
public sealed class SkiaImageProcessor : IImageProcessor
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const long MaxPixels = 25_000_000;
    public const int MinSide = 64;
    public const int MaxSide = 1600;

    private static readonly SemaphoreSlim Gate = new(4);

    public Result<PreparedImage> Prepare(byte[] data)
    {
        if (data.Length == 0)
        {
            return new Error(ErrorCodes.ImageInvalid, "The image is empty.", ErrorType.Validation);
        }

        if (data.Length > MaxBytes)
        {
            return new Error(ErrorCodes.ImageTooLarge, "The image is larger than 5 MB.", ErrorType.PayloadTooLarge);
        }

        if (!IsSupported(data))
        {
            return new Error(ErrorCodes.ImageUnsupportedType, "Only JPEG, PNG and WebP images are accepted.", ErrorType.Validation);
        }

        if (!Gate.Wait(TimeSpan.FromSeconds(10)))
        {
            return Error.Unavailable(ErrorCodes.RateLimited, "The server is busy processing images. Please retry shortly.");
        }

        try
        {
            using var skData = SKData.CreateCopy(data);
            using var codec = SKCodec.Create(skData);
            if (codec is null)
            {
                return new Error(ErrorCodes.ImageInvalid, "The image could not be read.", ErrorType.Validation);
            }

            var info = codec.Info;
            if ((long)info.Width * info.Height > MaxPixels)
            {
                return new Error(ErrorCodes.ImageTooLarge, "The image has more than 25 megapixels.", ErrorType.PayloadTooLarge);
            }

            if (Math.Min(info.Width, info.Height) < MinSide)
            {
                return new Error(ErrorCodes.ImageInvalid, $"The image must be at least {MinSide}×{MinSide} pixels.", ErrorType.Validation);
            }

            using var decoded = SKBitmap.Decode(codec, new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            if (decoded is null)
            {
                return new Error(ErrorCodes.ImageInvalid, "The image could not be decoded.", ErrorType.Validation);
            }

            using var oriented = Orient(decoded, codec.EncodedOrigin);
            using var scaled = Downscale(oriented);
            using var image = SKImage.FromBitmap(scaled);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            if (encoded is null)
            {
                return new Error(ErrorCodes.ImageInvalid, "The image could not be processed.", ErrorType.Validation);
            }

            return new PreparedImage(encoded.ToArray(), scaled.Width, scaled.Height, SHA256.HashData(data));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new Error(ErrorCodes.ImageInvalid, "The image could not be processed.", ErrorType.Validation);
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static bool IsSupported(ReadOnlySpan<byte> d) =>
        (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) // JPEG
        || (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A) // PNG
        || (d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P'); // WebP

    private static SKBitmap Downscale(SKBitmap source)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (longest <= MaxSide)
        {
            return source.Copy();
        }

        var ratio = (double)MaxSide / longest;
        var info = new SKImageInfo(Math.Max(1, (int)(source.Width * ratio)), Math.Max(1, (int)(source.Height * ratio)), SKColorType.Rgba8888, SKAlphaType.Opaque);
        return source.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)) ?? source.Copy();
    }

    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return source.Copy();
        }

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var w = source.Width;
        var h = source.Height;
        var target = new SKBitmap(swap ? h : w, swap ? w : h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(target);
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0);
        return target;
    }
}
