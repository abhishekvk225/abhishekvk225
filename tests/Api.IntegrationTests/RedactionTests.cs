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
}
