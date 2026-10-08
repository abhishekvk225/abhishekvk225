using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Common;
using NexaVerify.Application.Faces;
using NexaVerify.Contracts.Common;
using NexaVerify.Infrastructure.Faces;
using SkiaSharp;

namespace NexaVerify.Infrastructure.UnitTests.Faces;

public class FaceEngineTests
{
    private static byte[] Image(int w, int h, Func<int, int, byte> pixel, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg)
    {
        using var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = pixel(x, y);
                bitmap.SetPixel(x, y, new SKColor(v, v, v));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, 95).ToArray();
    }

    private static byte[] Pattern(int seed, int size = 160)
    {
        var levels = new byte[64];
        new Random(seed).NextBytes(levels);
        return Image(size, size, (x, y) => levels[(y * 8 / size * 8) + (x * 8 / size)]);
    }

    [Fact]
    public async Task The_processor_decides_the_type_from_the_bytes_and_strips_everything_else()
    {
        var processor = new SkiaImageProcessor();

        (await processor.PrepareAsync(Pattern(1), default)).IsSuccess.ShouldBeTrue();
        (await processor.PrepareAsync(Image(160, 160, (x, y) => (byte)(x + y), SKEncodedImageFormat.Png), default)).IsSuccess.ShouldBeTrue();

        var text = await processor.PrepareAsync("GIF89a....."u8.ToArray(), default);
        text.Error!.Code.ShouldBe(ErrorCodes.ImageUnsupportedType);
        (await processor.PrepareAsync([], default)).Error!.Code.ShouldBe(ErrorCodes.ImageInvalid);
        (await processor.PrepareAsync([0xFF, 0xD8, 0xFF, 1, 2, 3], default)).Error!.Code.ShouldBe(ErrorCodes.ImageInvalid);

        var tooBig = new byte[SkiaImageProcessor.MaxBytes + 1];
        tooBig[0] = 0xFF;
        (await processor.PrepareAsync(tooBig, default)).Error!.Type.ShouldBe(ErrorType.PayloadTooLarge);

        (await processor.PrepareAsync(Image(30, 30, (_, _) => 90), default)).Error!.Code.ShouldBe(ErrorCodes.ImageInvalid); // below the minimum side
    }

    [Fact]
    public async Task Large_images_are_scaled_down_and_always_come_back_as_plain_jpeg()
    {
        var processor = new SkiaImageProcessor();
        var prepared = (await processor.PrepareAsync(Image(3000, 2000, (x, y) => (byte)((x / 50) + (y / 50)), SKEncodedImageFormat.Png), default)).Value!;

        Math.Max(prepared.Width, prepared.Height).ShouldBe(SkiaImageProcessor.MaxSide);
        prepared.Jpeg[0].ShouldBe((byte)0xFF);
        prepared.Jpeg[1].ShouldBe((byte)0xD8);
        prepared.Sha256.Length.ShouldBe(32);
    }

    [Fact]
    public async Task The_mock_engine_scores_the_same_picture_high_and_unrelated_pictures_low()
    {
        var engine = new MockFaceEngine();
        var processor = new SkiaImageProcessor();

        async Task<float[]> Embed(byte[] bytes)
        {
            var image = (await processor.PrepareAsync(bytes, default)).Value!;
            var faces = await engine.DetectAsync(image, default);
            return await engine.ExtractAsync(image, faces[0], default);
        }

        var a = await Embed(Pattern(1));
        (await Embed(Pattern(1))).Length.ShouldBe(engine.Dimensions);
        engine.Similarity(a, await Embed(Pattern(1))).ShouldBeGreaterThan(0.99);
        engine.Similarity(a, await Embed(Pattern(2))).ShouldBeLessThan(0.5);
        engine.Similarity(a, new float[3]).ShouldBe(0); // mismatched sizes never match
    }

    [Fact]
    public async Task Scoring_keeps_the_best_template_per_person_and_orders_by_score()
    {
        var engine = new MockFaceEngine();
        float[] V(params float[] values)
        {
            var padded = new float[engine.Dimensions];
            values.CopyTo(padded, 0);
            return padded;
        }

        var probe = V(1, 0);
        var alice1 = new IndexedTemplate(Guid.NewGuid(), Guid.Parse("00000000-0000-0000-0000-00000000000a"), V(0, 1));
        var alice2 = new IndexedTemplate(Guid.NewGuid(), alice1.ProfileId, V(1, 0.1f));
        var bob = new IndexedTemplate(Guid.NewGuid(), Guid.Parse("00000000-0000-0000-0000-00000000000b"), V(1, 1));

        var ranked = Scoring.Rank(engine, probe, [alice1, bob, alice2]);

        ranked.Count.ShouldBe(2);
        ranked[0].Template.TemplateId.ShouldBe(alice2.TemplateId);
        ranked[0].Score.ShouldBeGreaterThan(ranked[1].Score);
        await Task.CompletedTask;
    }

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "t";

        public string ContentRootPath { get; set; } = ".";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Theory]
    [InlineData("Production", "mock", false, false)]
    [InlineData("Production", "mock", true, true)]
    [InlineData("Development", "mock", false, true)]
    [InlineData("Development", "nope", false, false)]
    public void The_mock_engine_is_refused_in_production_unless_explicitly_allowed(string environment, string provider, bool allow, bool valid)
    {
        var validator = new FaceEngineOptionsValidator(new Env { EnvironmentName = environment });
        validator.Validate(null, new FaceEngineOptions { Provider = provider, AllowMockInProduction = allow }).Succeeded.ShouldBe(valid);
    }
}
