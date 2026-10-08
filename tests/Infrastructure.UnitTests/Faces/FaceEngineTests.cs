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
    public void The_processor_decides_the_type_from_the_bytes_and_strips_everything_else()
    {
        var processor = new SkiaImageProcessor();

        processor.Prepare(Pattern(1)).IsSuccess.ShouldBeTrue();
        processor.Prepare(Image(160, 160, (x, y) => (byte)(x + y), SKEncodedImageFormat.Png)).IsSuccess.ShouldBeTrue();

        var text = processor.Prepare("GIF89a....."u8.ToArray());
        text.Error!.Code.ShouldBe(ErrorCodes.ImageUnsupportedType);
        processor.Prepare([]).Error!.Code.ShouldBe(ErrorCodes.ImageInvalid);
        processor.Prepare([0xFF, 0xD8, 0xFF, 1, 2, 3]).Error!.Code.ShouldBe(ErrorCodes.ImageInvalid);

        var tooBig = new byte[SkiaImageProcessor.MaxBytes + 1];
        tooBig[0] = 0xFF;
        processor.Prepare(tooBig).Error!.Type.ShouldBe(ErrorType.PayloadTooLarge);

        processor.Prepare(Image(30, 30, (_, _) => 90)).Error!.Code.ShouldBe(ErrorCodes.ImageInvalid); // below the minimum side
    }

    [Fact]
    public void Large_images_are_scaled_down_and_always_come_back_as_plain_jpeg()
    {
        var processor = new SkiaImageProcessor();
        var prepared = processor.Prepare(Image(3000, 2000, (x, y) => (byte)((x / 50) + (y / 50)), SKEncodedImageFormat.Png)).Value!;

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
            var image = processor.Prepare(bytes).Value!;
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
