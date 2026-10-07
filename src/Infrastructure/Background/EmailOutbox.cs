using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexaVerify.Application.Abstractions;

namespace NexaVerify.Infrastructure.Background;

/// <summary>In-process queue of outgoing mail. (A durable outbox table replaces it with the notifications module.)</summary>
public sealed class EmailOutbox : IEmailOutbox
{
    private readonly Channel<EmailMessage> _channel = Channel.CreateBounded<EmailMessage>(new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    private readonly ILogger<EmailOutbox> _logger;

    public EmailOutbox(ILogger<EmailOutbox> logger)
    {
        _logger = logger;
    }

    public ChannelReader<EmailMessage> Reader => _channel.Reader;

    public void Enqueue(EmailMessage message)
    {
        if (!_channel.Writer.TryWrite(message))
        {
            _logger.LogError("Email queue is full; dropping a message for {Recipient}", "(redacted)");
        }
    }
}

/// <summary>Sends queued mail through <see cref="IEmailSender"/> with a few retries; failures never reach the original request.</summary>
public sealed class EmailDispatcher : BackgroundService
{
    private static readonly TimeSpan[] DefaultRetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    private readonly EmailOutbox _outbox;
    private readonly IEmailSender _sender;
    private readonly ILogger<EmailDispatcher> _logger;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;

    public EmailDispatcher(EmailOutbox outbox, IEmailSender sender, ILogger<EmailDispatcher> logger, IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _retryDelays = retryDelays ?? DefaultRetryDelays;
        _outbox = outbox;
        _sender = sender;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in _outbox.Reader.ReadAllAsync(stoppingToken))
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await _sender.SendAsync(message, stoppingToken);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (attempt >= _retryDelays.Count)
                    {
                        _logger.LogError(ex, "Giving up sending email '{Subject}' after {Attempts} attempts", message.Subject, attempt + 1);
                        break;
                    }

                    await Task.Delay(_retryDelays[attempt], stoppingToken);
                }
            }
        }
    }
}
