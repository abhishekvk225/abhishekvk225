using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexaVerify.Api.Http;
using NexaVerify.Application.Dashboards;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>How CSV streaming behaves when the data source fails: before the first byte (a normal error) and after it (an aborted transfer, never a clean-looking truncated file).</summary>
public class CsvStreamResultTests
{
    private sealed class Lifetime : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }

        public bool Aborted { get; private set; }

        public void Abort() => Aborted = true;
    }

    private sealed class Capture : ILoggerProvider
    {
        public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(Capture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    private static (DefaultHttpContext Http, MemoryStream Body, Lifetime Lifetime, Capture Log) NewContext(CancellationToken aborted = default)
    {
        var log = new Capture();
        var services = new ServiceCollection().AddLogging(b => b.AddProvider(log)).BuildServiceProvider();
        var body = new MemoryStream();
        var lifetime = new Lifetime { RequestAborted = aborted };
        var http = new DefaultHttpContext { RequestServices = services };
        http.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        http.Response.Body = body;
        return (http, body, lifetime, log);
    }

    private static async IAsyncEnumerable<string> Lines(int rows, int failAfter = -1, string failure = "secret row content")
    {
        yield return "date,requests\r\n";
        for (var i = 0; i < rows; i++)
        {
            if (i == failAfter)
            {
                throw new InvalidOperationException(failure);
            }

            await Task.Yield();
            yield return $"2026-01-{i + 1:00},{i}\r\n";
        }
    }

    private static Task RunAsync(DefaultHttpContext http, IAsyncEnumerable<string> lines) =>
        new CsvStreamResult(new UsageReportStream("usage.csv", lines)).ExecuteResultAsync(new ActionContext(http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));

    [Fact]
    public async Task A_healthy_export_is_utf8_with_a_byte_order_mark_and_every_row()
    {
        var (http, body, lifetime, _) = NewContext();

        await RunAsync(http, Lines(5));

        var bytes = body.ToArray();
        bytes.Take(3).ShouldBe([(byte)0xEF, (byte)0xBB, (byte)0xBF]);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(6);
        http.Response.StatusCode.ShouldBe(200);
        http.Response.ContentType.ShouldBe("text/csv; charset=utf-8");
        http.Response.Headers.CacheControl.ToString().ShouldBe("no-store");
        lifetime.Aborted.ShouldBeFalse();
    }

    [Fact]
    public async Task An_empty_report_still_sends_the_header()
    {
        var (http, body, _, _) = NewContext();

        await RunAsync(http, Lines(0));

        Encoding.UTF8.GetString(body.ToArray()).ShouldContain("date,requests");
    }

    [Fact]
    public async Task A_failure_on_the_first_data_row_is_raised_before_any_byte_is_sent_so_it_becomes_a_normal_error_response()
    {
        var (http, body, lifetime, _) = NewContext();

        await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(http, Lines(5, failAfter: 0)));

        body.Length.ShouldBe(0);
        http.Response.HasStarted.ShouldBeFalse();
        http.Response.ContentType.ShouldBeNull();
        lifetime.Aborted.ShouldBeFalse();
    }

    [Fact]
    public async Task A_failure_after_the_response_started_aborts_the_transfer_and_logs_without_row_content()
    {
        var (http, body, lifetime, log) = NewContext();

        await RunAsync(http, Lines(10, failAfter: 4)); // does not throw: the status line is gone, the connection is cut instead

        lifetime.Aborted.ShouldBeTrue("the client must see a failed transfer, not a file that merely ends early");
        body.Length.ShouldBeGreaterThan(0);
        var error = log.Entries.ShouldHaveSingleItem();
        error.Level.ShouldBe(LogLevel.Error);
        error.Message.ShouldContain("usage.csv");
        error.Message.ShouldNotContain("secret row content");
    }

    [Fact]
    public async Task A_client_that_goes_away_is_not_an_error()
    {
        using var cancelled = new CancellationTokenSource();
        var (http, _, lifetime, log) = NewContext(cancelled.Token);
        cancelled.Cancel();

        await RunAsync(http, Lines(5));

        lifetime.Aborted.ShouldBeFalse();
        log.Entries.ShouldBeEmpty();
    }
}
