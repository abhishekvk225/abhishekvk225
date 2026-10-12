using Microsoft.Extensions.Logging;

namespace NexaVerify.TestSupport.Verification;

/// <summary>Collects every formatted log message (and exception text) so tests can prove nothing sensitive is logged.</summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return _messages.ToList();
            }
        }
    }

    public string All => string.Join('\n', Messages);

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public ILoggerFactory Factory() => LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(this));

    public void Dispose()
    {
    }

    private sealed class Capture(LogCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._messages)
            {
                owner._messages.Add(formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception));
            }
        }
    }
}
