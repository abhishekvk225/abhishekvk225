using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<SkiaImageProcessor>? _logger;

    public SkiaImageProcessor()
    {
    }

    public SkiaImageProcessor(ILogger<SkiaImageProcessor> logger)
    {
        _logger = logger;
    }

    public const int MaxBytes = 5 * 1024 * 1024;
    public const long MaxPixels = 25_000_000;
    public const int MinSide = 64;
    public const int MaxSide = 1600;

    private const int MaxConcurrentDecodes = 3;
    private const int MaxWaiting = 12;

    private static readonly SemaphoreSlim Gate = new(MaxConcurrentDecodes);
    private static int _waiting;

    public async Task<Result<PreparedImage>> PrepareAsync(byte[] data, CancellationToken cancellationToken)
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

        // Bounded queue: when the node is saturated, shed load immediately instead of parking request threads (one tenant must not
        // be able to starve the others by flooding the decoder).
        if (Interlocked.Increment(ref _waiting) > MaxWaiting)
        {
            Interlocked.Decrement(ref _waiting);
            return Busy();
        }

        var entered = false;
        try
        {
            entered = await Gate.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }

        if (!entered)
        {
            return Busy();
        }

        try
        {
            return await Task.Run(() => Decode(data), cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static Error Busy() => Error.Unavailable(ErrorCodes.RateLimited, "The server is busy processing images. Please retry shortly.");

    private Result<PreparedImage> Decode(byte[] data)
    {
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

            // JPEG can be decoded at 1/2, 1/4 or 1/8 size straight from the DCT: a 25 MP photo never needs a 100 MB bitmap.
            var longest = Math.Max(info.Width, info.Height);
            var target = longest > MaxSide ? codec.GetScaledDimensions((float)MaxSide / longest) : info.Size;
            if (target.Width < MinSide || target.Height < MinSide)
            {
                target = info.Size;
            }

            using var decoded = SKBitmap.Decode(codec, new SKImageInfo(target.Width, target.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            if (decoded is null)
            {
                return new Error(ErrorCodes.ImageInvalid, "The image could not be decoded.", ErrorType.Validation);
            }

            var oriented = Orient(decoded, codec.EncodedOrigin);
            var scaled = Downscale(oriented);
            try
            {
                using var image = SKImage.FromBitmap(scaled);
                using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
                if (encoded is null)
                {
                    return new Error(ErrorCodes.ImageInvalid, "The image could not be processed.", ErrorType.Validation);
                }

                return new PreparedImage(encoded.ToArray(), scaled.Width, scaled.Height, SHA256.HashData(data));
            }
            finally
            {
                if (!ReferenceEquals(scaled, oriented))
                {
                    scaled.Dispose();
                }

                if (!ReferenceEquals(oriented, decoded))
                {
                    oriented.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogWarning(ex, "Image processing failed");
            return new Error(ErrorCodes.ImageInvalid, "The image could not be processed.", ErrorType.Validation);
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
            return source;
        }

        var ratio = (double)MaxSide / longest;
        var info = new SKImageInfo(Math.Max(1, (int)(source.Width * ratio)), Math.Max(1, (int)(source.Height * ratio)), SKColorType.Rgba8888, SKAlphaType.Opaque);
        return source.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)) ?? source;
    }

    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return source;
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
