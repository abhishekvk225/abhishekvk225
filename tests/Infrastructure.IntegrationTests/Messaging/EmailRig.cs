using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NexaVerify.Application;
using NexaVerify.Application.Messaging;
using NexaVerify.Domain.Messaging;
using NexaVerify.Infrastructure;
using NexaVerify.Infrastructure.Background;
using NexaVerify.Infrastructure.Messaging;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.IntegrationTests.Messaging;

internal sealed class TestEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";

    public string ApplicationName { get; set; } = "NexaVerify.Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed record LogLine(LogLevel Level, int EventId, string Message);

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<LogLine> _lines = [];

    public IReadOnlyList<LogLine> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    private sealed class Capture(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._lines)
            {
                owner._lines.Add(new LogLine(logLevel, eventId.Id, formatter(state, exception)));
            }
        }
    }
}

/// <summary>A transport whose behaviour each test scripts; it records what it was asked to send.</summary>
internal sealed class ScriptedTransport : IEmailTransport
{
    private readonly object _lock = new();
    private readonly List<OutgoingEmail> _sent = [];

    public Func<OutgoingEmail, CancellationToken, Task>? Behaviour { get; set; }

    public IReadOnlyList<OutgoingEmail> Sent
    {
        get
        {
            lock (_lock)
            {
                return [.. _sent];
            }
        }
    }

    public async Task SendAsync(OutgoingEmail message, CancellationToken cancellationToken)
    {
        if (Behaviour is { } behaviour)
        {
            await behaviour(message, cancellationToken);
        }

        lock (_lock)
        {
            _sent.Add(message);
        }
    }
}

/// <summary>The real email stack (outbox sender, store, dispatcher, repositories) on a real migrated SQL Server database; only the mail server is a stand-in.</summary>
internal sealed class EmailRig : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _connection;

    private readonly ServiceProvider _provider;

    private EmailRig(ServiceProvider provider, string connectionString, FakeTimeProvider time, ScriptedTransport transport, CapturingLoggerProvider logs, Action<EmailOptions> configure)
    {
        _provider = provider;
        ConnectionString = connectionString;
        Time = time;
        Transport = transport;
        Logs = logs;
        Configure = configure;
    }

    public string ConnectionString { get; }

    public FakeTimeProvider Time { get; }

    public ScriptedTransport Transport { get; }

    public CapturingLoggerProvider Logs { get; }

    public Action<EmailOptions> Configure { get; }

    public IServiceProvider Services => _provider;

    public EmailDispatcher Dispatcher => _provider.GetRequiredService<EmailDispatcher>();

    /// <summary>A second, independent dispatcher over the same database (another node).</summary>
    public EmailDispatcher AnotherNode() => new(
        _provider.GetRequiredService<IServiceScopeFactory>(), Transport, _provider.GetRequiredService<IOptions<EmailOptions>>(),
        _provider.GetRequiredService<ILogger<EmailDispatcher>>(), Time, () => 0.5);

    public static async Task<EmailRig> CreateAsync(SqlServerFixture fixture, Action<EmailOptions>? configure = null, IEmailTransport? transport = null)
    {
        await Gate.WaitAsync();
        try
        {
            if (_connection is null)
            {
                _connection = await fixture.CreateDatabaseAsync();
                await DatabaseBootstrap.MigrateAsync(_connection);
            }
        }
        finally
        {
            Gate.Release();
        }

        await using (var connection = new SqlConnection(_connection))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM tenancy.OutboundEmails; DELETE FROM tenancy.EmailSuppressions;";
            await command.ExecuteNonQueryAsync();
        }

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        var scripted = new ScriptedTransport();
        var logs = new CapturingLoggerProvider();
        var settings = new Dictionary<string, string?>(DatabaseBootstrap.BaseSettings(_connection))
        {
            ["Portal:PublicBaseUrl"] = "https://portal.example.com",
            ["Email:From:Address"] = "no-reply@example.com",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestEnvironment());
        services.AddSingleton<TimeProvider>(time);
        services.AddApplication();
        services.AddInfrastructure(configuration);
        services.AddSingleton<IEmailTransport>(transport ?? scripted);
        var tweak = configure ?? (_ => { });
        services.PostConfigure<EmailOptions>(o =>
        {
            o.Dispatcher.BaseDelaySeconds = 30;
            o.Dispatcher.JitterPercent = 20;
            tweak(o);
        });
        services.AddSingleton<EmailDispatcher>(sp => new EmailDispatcher(
            sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IEmailTransport>(), sp.GetRequiredService<IOptions<EmailOptions>>(),
            sp.GetRequiredService<ILogger<EmailDispatcher>>(), time, () => 0.5));
        return new EmailRig(services.BuildServiceProvider(), _connection, time, scripted, logs, tweak);
    }

    /// <summary>Queues mails the way the application does: staged in a unit of work, stored by its save.</summary>
    public async Task<IReadOnlyList<Guid>> EnqueueAsync(params EmailRequest[] requests)
    {
        await using var scope = _provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var ids = new List<Guid>();
        foreach (var request in requests)
        {
            ids.Add((await sender.SendAsync(request, default))!.Value);
        }

        await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync();
        return ids;
    }

    public static EmailRequest Reset(string to, string name = "Ada Lovelace") =>
        new EmailRequest(EmailTemplate.PasswordReset, to, name).Text("name", name).Number("minutes", 30)
            .Link("action", "reset-password", ("email", to), ("token", "TOKEN" + Guid.NewGuid().ToString("N")));

    public static EmailRequest Changed(string to) =>
        new EmailRequest(EmailTemplate.PasswordChanged, to, "Ada").Text("name", "Ada").Text("when", "now").Text("how", "test").Link("reset", "forgot-password");

    public async Task<OutboundEmail> GetAsync(Guid id)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().OutboundEmails.AsNoTracking().FirstAsync(e => e.Id == id);
    }

    public async Task<int> ExecuteAsync(string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
