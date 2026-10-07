using NexaVerify.Api.Logging;
using NexaVerify.Contracts.Common;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NexaVerify.Api.IntegrationTests;

public class RedactionTests
{
    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    public sealed class CreateApiKeyResponseDto
    {
        public string Name { get; set; } = "ci";

        [Sensitive]
        public string Key { get; set; } = "nxv_live_SUPERSECRETVALUE";
    }

    public sealed class LoginRequestDto
    {
        public string Email { get; set; } = "a@b.c";

        public string Password { get; set; } = "P@ssw0rd!";

        public string RefreshToken { get; set; } = "tok_123";
    }

    private static (Serilog.ILogger Logger, CaptureSink Sink) Create()
    {
        var sink = new CaptureSink();
        var logger = new LoggerConfiguration().Destructure.With<SensitiveDataDestructuringPolicy>().WriteTo.Sink(sink).CreateLogger();
        return (logger, sink);
    }

    [Fact]
    public void Properties_marked_sensitive_are_masked()
    {
        var (logger, sink) = Create();

        logger.Information("created {@Response}", new CreateApiKeyResponseDto());

        var rendered = sink.Events.Single().RenderMessage();
        rendered.ShouldNotContain("SUPERSECRETVALUE");
        rendered.ShouldContain(SensitiveDataDestructuringPolicy.Mask);
        rendered.ShouldContain("ci");
    }

    [Fact]
    public void Credential_like_property_names_are_masked_even_without_an_attribute()
    {
        var (logger, sink) = Create();

        logger.Information("login {@Request}", new LoginRequestDto());

        var rendered = sink.Events.Single().RenderMessage();
        rendered.ShouldNotContain("P@ssw0rd!");
        rendered.ShouldNotContain("tok_123");
        rendered.ShouldContain("a@b.c");
    }

    public sealed class UploadDto
    {
        public string Name { get; set; } = "photo.jpg";

        public byte[] Content { get; set; } = new byte[4096];

        public Dictionary<string, string> Headers { get; set; } = new() { ["Authorization"] = "Bearer abc.def.ghi", ["Accept"] = "json" };
    }

    [Fact]
    public void Binary_payloads_and_secret_dictionary_entries_are_masked()
    {
        var (logger, sink) = Create();

        logger.Information("upload {@Upload}", new UploadDto());

        var rendered = sink.Events.Single().RenderMessage();
        rendered.ShouldNotContain("0000");
        rendered.ShouldContain("Content: \"***\"");
        rendered.ShouldNotContain("abc.def.ghi");
        rendered.ShouldContain("json");
    }

    [Fact]
    public void Plain_dictionaries_with_sensitive_keys_are_masked()
    {
        var (logger, sink) = Create();

        logger.Information("bag {@Bag}", new Dictionary<string, string> { ["password"] = "pw1", ["user"] = "ada" });

        var rendered = sink.Events.Single().RenderMessage();
        rendered.ShouldNotContain("pw1");
        rendered.ShouldContain("ada");
    }

    [Theory]
    [InlineData("Password", true)]
    [InlineData("RefreshToken", true)]
    [InlineData("ConnectionString", true)]
    [InlineData("Hash", true)]
    [InlineData("Code", true)]
    [InlineData("Name", false)]
    [InlineData("Pinned", false)]
    public void Sensitive_name_detection(string name, bool sensitive)
    {
        SensitiveDataDestructuringPolicy.IsSensitiveName(name).ShouldBe(sensitive);
    }

    [Fact]
    public void Exceptions_are_left_to_serilog()
    {
        var (logger, sink) = Create();

        logger.Error(new NexaVerify.Domain.Common.DomainException("X", "bad"), "failed {@Ex}", new NexaVerify.Domain.Common.DomainException("Y", "worse"));

        sink.Events.Single().Exception.ShouldNotBeNull();
    }
}
