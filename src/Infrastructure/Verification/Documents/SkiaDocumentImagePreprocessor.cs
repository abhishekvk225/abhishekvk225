using Microsoft.Extensions.Options;
using NexaVerify.Application.Common;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Contracts.Common;
using SkiaSharp;

namespace NexaVerify.Infrastructure.Verification.Documents;

/// <summary>
/// Prepares a page image for OCR: header-only pixel check before decoding (decompression-bomb guard), EXIF orientation, grayscale,
/// longest side bounded, re-encoded as PNG so no metadata or appended payload reaches the OCR engine. Full resolution is kept up to
/// the bound (this is deliberately not the face pipeline, which downsamples to 1600 px).
/// </summary>
public sealed class SkiaDocumentImagePreprocessor : IDocumentImagePreprocessor
{
    private static readonly SKColorFilter Gray = SKColorFilter.CreateColorMatrix(
    [
        0.299f, 0.587f, 0.114f, 0, 0,
        0.299f, 0.587f, 0.114f, 0, 0,
        0.299f, 0.587f, 0.114f, 0, 0,
        0, 0, 0, 1, 0,
    ]);

    private readonly VerificationOptions _options;
    private readonly DocumentPipelineOptions _pipeline;

    public SkiaDocumentImagePreprocessor(IOptions<VerificationOptions> options, IOptions<DocumentPipelineOptions> pipeline)
    {
        _options = options.Value;
        _pipeline = pipeline.Value;
    }

    public Task<Result<byte[]>> PrepareForOcrAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
    {
        var bytes = image.ToArray();
        return Task.Run(() => Prepare(bytes), cancellationToken);
    }

    private Result<byte[]> Prepare(byte[] bytes)
    {
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > _options.Upload.MaxImagePixels)
            {
                return new Error(ErrorCodes.DocumentUnreadable, "The image could not be read.", ErrorType.UnprocessableEntity);
            }

            using var decoded = SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            if (decoded is null)
            {
                return new Error(ErrorCodes.DocumentUnreadable, "The image could not be decoded.", ErrorType.UnprocessableEntity);
            }

            var swap = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            var w = swap ? decoded.Height : decoded.Width;
            var h = swap ? decoded.Width : decoded.Height;
            var scale = Math.Min(1.0, (double)_pipeline.OcrMaxImageSide / Math.Max(w, h));
            var tw = Math.Max(1, (int)(w * scale));
            var th = Math.Max(1, (int)(h * scale));

            using var target = new SKBitmap(tw, th, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(target))
            {
                canvas.Clear(SKColors.White);
                canvas.Scale((float)tw / w, (float)th / h);
                canvas.Concat(OrientationMatrix(codec.EncodedOrigin, decoded.Width, decoded.Height));
                using var paint = new SKPaint { ColorFilter = Gray };
                canvas.DrawBitmap(decoded, 0, 0, paint);
            }

            using var img = SKImage.FromBitmap(target);
            using var png = img.Encode(SKEncodedImageFormat.Png, 100);
            return png.ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new Error(ErrorCodes.DocumentUnreadable, "The image could not be processed.", ErrorType.UnprocessableEntity);
        }
    }

    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
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
}
