using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using NexaVerify.Domain.Messaging;

namespace NexaVerify.Infrastructure.Messaging;

/// <summary>A rendered message on its way to the mail server.</summary>
public sealed record OutgoingEmail(
    Guid Id, string ToAddress, string? ToName, string Subject, string TextBody, string HtmlBody, string MessageId, string CorrelationId,
    DateTime CreatedAtUtc, EmailTemplate Template);

public enum EmailFailureKind
{
    /// <summary>Network trouble, timeout, 4xx reply, authentication or TLS problem: worth retrying later.</summary>
    Transient = 0,

    /// <summary>The server permanently refused this recipient (unknown mailbox, bad address): retrying is pointless and the address is suppressed.</summary>
    PermanentRecipient,

    /// <summary>Permanently refused for another reason (sender or content rejected, unusable message): retrying is pointless.</summary>
    Permanent,
}

/// <summary>A delivery failure, already classified. The message is safe to store (no addresses, secrets or content).</summary>
public sealed class EmailSendException : Exception
{
    public EmailSendException(EmailFailureKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public EmailFailureKind Kind { get; }

    public bool IsPermanent => Kind != EmailFailureKind.Transient;
}

/// <summary>Delivers one message to the outside world. Throws <see cref="EmailSendException"/> on failure and <see cref="OperationCanceledException"/> on shutdown.</summary>
public interface IEmailTransport
{
    Task SendAsync(OutgoingEmail message, CancellationToken cancellationToken);
}

/// <summary>Development/Testing transport: the mail is written to the log instead of being sent.</summary>
public sealed partial class LoggingEmailTransport : IEmailTransport
{
    private readonly ILogger<LoggingEmailTransport> _logger;
    private readonly EmailOptions _options;

    public LoggingEmailTransport(ILogger<LoggingEmailTransport> logger, IOptions<EmailOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public Task SendAsync(OutgoingEmail message, CancellationToken cancellationToken)
    {
        if (_options.LogBodies)
        {
            LogWithBody(message.ToAddress, message.Subject, message.TextBody);
        }
        else
        {
            LogSubject(message.ToAddress, message.Subject);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 8301, Level = LogLevel.Information, Message = "Email (logging provider, not sent) to {To}: {Subject}")]
    private partial void LogSubject(string to, string subject);

    [LoggerMessage(EventId = 8302, Level = LogLevel.Information, Message = "Email (logging provider, not sent) to {To}: {Subject}\n{Body}")]
    private partial void LogWithBody(string to, string subject, string body);
}

/// <summary>Builds the MIME message: multipart/alternative (plain text + HTML), identifying headers, no tracking.</summary>
public static class EmailMime
{
    public const string CorrelationHeader = "X-NexaVerify-Correlation";

    public static MimeMessage Build(OutgoingEmail message, EmailOptions options)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(EmailText.DisplayName(options.From.Name), options.From.Address));
        mime.To.Add(new MailboxAddress(EmailText.DisplayName(message.ToName), message.ToAddress));
        if (!string.IsNullOrWhiteSpace(options.ReplyTo))
        {
            mime.ReplyTo.Add(new MailboxAddress((string?)null, options.ReplyTo));
        }

        mime.Subject = EmailText.Subject(message.Subject);
        mime.MessageId = message.MessageId;
        mime.Date = new DateTimeOffset(DateTime.SpecifyKind(message.CreatedAtUtc, DateTimeKind.Utc));
        mime.Headers.Add(CorrelationHeader, EmailText.SingleLine(message.CorrelationId));
        mime.Headers.Add("Auto-Submitted", "auto-generated");
        mime.Headers.Add("X-Auto-Response-Suppress", "All");

        var body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody };
        mime.Body = body.ToMessageBody();
        return mime;
    }
}

/// <summary>Real delivery through any SMTP server (Amazon SES, SendGrid, Mailgun, Gmail relay, Postfix...) with MailKit. Certificates are always validated.</summary>
public sealed class SmtpEmailTransport : IEmailTransport
{
    private readonly EmailOptions _options;

    public SmtpEmailTransport(IOptions<EmailOptions> options)
    {
        _options = options.Value;
    }

    public async Task SendAsync(OutgoingEmail message, CancellationToken cancellationToken)
    {
        var smtp = _options.Smtp;
        var timeout = TimeSpan.FromSeconds(smtp.TimeoutSeconds);

        MimeMessage mime;
        try
        {
            mime = EmailMime.Build(message, _options);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or ParseException)
        {
            throw new EmailSendException(EmailFailureKind.Permanent, "The message could not be built (invalid address or header).", ex);
        }

        // MailKit's own Timeout bounds each socket operation; this bounds the whole send, so a trickling server cannot hold a slot forever.
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(timeout * 4);
        try
        {
            using var client = new SmtpClient
            {
                Timeout = (int)timeout.TotalMilliseconds,
                CheckCertificateRevocation = true,
            };

            // Deliberately no ServerCertificateValidationCallback: the platform's default chain, host name and revocation checks apply.
            if (!string.IsNullOrWhiteSpace(smtp.LocalDomain))
            {
                client.LocalDomain = smtp.LocalDomain;
            }

            await client.ConnectAsync(smtp.Host, smtp.Port, ToMailKit(smtp.Security), overall.Token);
            if (!string.IsNullOrEmpty(smtp.Username))
            {
                await client.AuthenticateAsync(smtp.Username, smtp.Password ?? string.Empty, overall.Token);
            }

            await client.SendAsync(mime, overall.Token);
            await client.DisconnectAsync(quit: true, overall.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // shutdown: not the server's fault
        }
        catch (Exception ex)
        {
            throw SmtpFailureClassifier.Classify(ex, _options);
        }
    }

    private static SecureSocketOptions ToMailKit(SmtpSecurity security) => security switch
    {
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.None => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls, // mandatory: MailKit fails the connection when the server does not offer STARTTLS
    };
}

/// <summary>
/// Decides what a failed send means. Permanent (a 5xx answer that no retry can change) and transient (4xx, network, timeout,
/// authentication or TLS trouble that an operator or time can fix) are told apart here, in one place; the stored text never contains
/// the address, the mail content or the configured credentials.
/// </summary>
public static class SmtpFailureClassifier
{
    public static EmailSendException Classify(Exception exception, EmailOptions options)
    {
        string?[] secrets = [options.Smtp.Password, options.Smtp.Username];
        string Safe(string text) => EmailText.Error(text, OutboundEmail.ErrorMaxLength, secrets);

        switch (exception)
        {
            case EmailSendException already:
                return already;

            case SmtpCommandException command:
            {
                var code = (int)command.StatusCode;
                var reply = Safe($"SMTP {code} ({command.ErrorCode}): {command.Message}");
                if (code is >= 400 and < 500)
                {
                    return new EmailSendException(EmailFailureKind.Transient, reply, exception);
                }

                if (command.ErrorCode == SmtpErrorCode.RecipientNotAccepted && IsRecipientRefusal(code, command.Message))
                {
                    return new EmailSendException(EmailFailureKind.PermanentRecipient, reply, exception);
                }

                // 5.2.2 / 452-like "mailbox full" is reported as 552 by some servers but clears up with time.
                if (command.Message.Contains("5.2.2", StringComparison.Ordinal))
                {
                    return new EmailSendException(EmailFailureKind.Transient, reply, exception);
                }

                return new EmailSendException(EmailFailureKind.Permanent, reply, exception);
            }

            case AuthenticationException:
                return new EmailSendException(EmailFailureKind.Transient, Safe("SMTP authentication failed. Check Email:Smtp:Username and Password."), exception);

            case SslHandshakeException:
                return new EmailSendException(EmailFailureKind.Transient, Safe("The TLS connection to the mail server was refused: its certificate is not trusted, does not match the host name, or is expired."), exception);

            case SmtpProtocolException or ServiceNotConnectedException or ServiceNotAuthenticatedException or IOException or SocketException or TimeoutException:
                return new EmailSendException(EmailFailureKind.Transient, Safe($"The mail server could not be reached or broke the connection ({exception.GetType().Name})."), exception);

            case OperationCanceledException:
                return new EmailSendException(EmailFailureKind.Transient, "The mail server did not answer in time.", exception);

            case FormatException or ArgumentException or ParseException:
                return new EmailSendException(EmailFailureKind.Permanent, "The message could not be sent as built (invalid address or header).", exception);

            default:
                return new EmailSendException(EmailFailureKind.Transient, Safe($"Unexpected delivery error ({exception.GetType().Name})."), exception);
        }
    }

    /// <summary>"No such mailbox" style refusals (550, 551, 553, 554 and the enhanced 5.1.x destination codes). Policy refusals such as 5.7.x do not suppress the address.</summary>
    private static bool IsRecipientRefusal(int code, string text) =>
        ((code is 550 or 551 or 553 or 554 or 501) && !text.Contains("5.7.", StringComparison.Ordinal))
        || text.Contains("5.1.", StringComparison.Ordinal);
}
