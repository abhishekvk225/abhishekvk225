using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;

namespace NexaVerify.Infrastructure.Messaging;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Development only: log the full message body (contains reset links!). Never enable in production.</summary>
    public bool LogBodies { get; set; }
}

/// <summary>
/// Default sender until SMTP delivery arrives with the notifications module: logs that a mail was produced
/// (recipient + subject), and the body only when explicitly enabled for development.
/// </summary>
public sealed class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;
    private readonly EmailOptions _options;

    public LoggingEmailSender(ILogger<LoggingEmailSender> logger, IOptions<EmailOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (_options.LogBodies)
        {
            _logger.LogInformation("EMAIL to {To}: {Subject}\n{Body}", message.To, message.Subject, message.Body);
        }
        else
        {
            _logger.LogInformation("Email queued for {To}: {Subject}", message.To, message.Subject);
        }

        return Task.CompletedTask;
    }
}
