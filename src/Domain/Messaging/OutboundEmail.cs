using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Messaging;

/// <summary>
/// Pending: queued (or waiting for its next retry). Sending: leased by one dispatcher. Sent: handed to the mail server.
/// Failed: permanently refused (a recipient or content problem retrying cannot fix). Abandoned: transient failures used up every attempt.
/// </summary>
public enum EmailStatus
{
    Pending = 0,
    Sending,
    Sent,
    Failed,
    Abandoned,
}

/// <summary>The email templates the platform sends. The renderer owns the wording; this is only the stable key stored with each message.</summary>
public enum EmailTemplate
{
    VerifyEmail = 0,
    AlreadyRegistered,
    PasswordReset,
    PasswordChanged,
    UserInvite,
    MfaEnabled,
    MfaDisabled,
    MfaRecoveryCodeUsed,
    LicenseLowBalance,
    LicenseExpiring,
    PaymentReceipt,
    RefundNotice,
    ContactRequest,
    WebhookEndpointDisabled,
    AdminTest,
}

/// <summary>
/// One rendered email waiting for (or past) delivery. Written in the same transaction as the business change that caused it, then
/// delivered by a background dispatcher, so a request never waits for the mail server and a rolled-back change never sends mail.
/// Platform data: it belongs to no tenant. Bodies are kept only as long as they are useful (see <see cref="MarkSent"/>).
/// </summary>
public sealed class OutboundEmail : Entity
{
    public const int AddressMaxLength = 320;
    public const int NameMaxLength = 150;
    public const int SubjectMaxLength = 200;
    public const int ErrorMaxLength = 300;
    public const int CorrelationMaxLength = 64;
    public const int MessageIdMaxLength = 200;

    private OutboundEmail()
    {
    }

    public string ToAddress { get; private set; } = string.Empty;

    /// <summary>The domain part of <see cref="ToAddress"/>, lower case: the key for the per-recipient-domain throttle.</summary>
    public string ToDomain { get; private set; } = string.Empty;

    public string? ToName { get; private set; }

    public string Subject { get; private set; } = string.Empty;

    public string? TextBody { get; private set; }

    public string? HtmlBody { get; private set; }

    public EmailTemplate Template { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    /// <summary>RFC 5322 Message-ID, fixed at enqueue time so a retry is recognisably the same message.</summary>
    public string MessageId { get; private set; } = string.Empty;

    /// <summary>The body carries a one-time token (verification, reset, invitation): it is erased the moment the mail is sent.</summary>
    public bool HasOneTimeToken { get; private set; }

    public EmailStatus Status { get; private set; }

    /// <summary>Delivery attempts started (counted when a dispatcher claims the message, so a crash loop still ends in abandonment).</summary>
    public int Attempts { get; private set; }

    public DateTime NextAttemptAt { get; private set; }

    public DateTime? LastAttemptAt { get; private set; }

    /// <summary>Identifies the dispatcher claim that currently owns the message; also the concurrency token, so a node that lost its lease cannot overwrite the result.</summary>
    public Guid? LeaseToken { get; private set; }

    public DateTime? LeaseUntil { get; private set; }

    public string? LastError { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? SentAt { get; private set; }

    public DateTime? BodiesPurgedAt { get; private set; }

    public bool HasBodies => TextBody is not null || HtmlBody is not null;

    public static OutboundEmail Queue(
        string toAddress, string? toName, string subject, string textBody, string htmlBody, EmailTemplate template,
        string correlationId, string messageId, bool hasOneTimeToken, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(toAddress) || toAddress.Length > AddressMaxLength || toAddress.Any(char.IsControl))
        {
            throw new DomainException("EMAIL_RECIPIENT_INVALID", "A valid recipient address is required.");
        }

        var at = toAddress.LastIndexOf('@');
        if (at < 1 || at == toAddress.Length - 1)
        {
            throw new DomainException("EMAIL_RECIPIENT_INVALID", "A valid recipient address is required.");
        }

        return new OutboundEmail
        {
            ToAddress = toAddress,
            ToDomain = toAddress[(at + 1)..].ToLowerInvariant(),
            ToName = string.IsNullOrWhiteSpace(toName) ? null : toName.Trim(),
            Subject = subject,
            TextBody = textBody,
            HtmlBody = htmlBody,
            Template = template,
            CorrelationId = correlationId,
            MessageId = messageId,
            HasOneTimeToken = hasOneTimeToken,
            Status = EmailStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
        };
    }

    /// <summary>Successful delivery to the mail server. A body holding a one-time token is erased now: it has done its job and must not sit in the table.</summary>
    public void MarkSent(DateTime now)
    {
        Status = EmailStatus.Sent;
        SentAt = now;
        LastError = null;
        LeaseToken = null;
        LeaseUntil = null;
        if (HasOneTimeToken)
        {
            PurgeBodies(now);
        }
    }

    /// <summary>
    /// Records a failed attempt. A permanent failure ends the message (Failed); a transient one schedules the retry after
    /// <paramref name="retryDelay"/>, or abandons the message once <paramref name="maxAttempts"/> attempts were used.
    /// </summary>
    public void RecordFailure(bool permanent, string sanitisedError, DateTime now, int maxAttempts, TimeSpan retryDelay)
    {
        LastError = Truncate(sanitisedError, ErrorMaxLength);
        LeaseToken = null;
        LeaseUntil = null;
        if (permanent)
        {
            Status = EmailStatus.Failed;
        }
        else if (Attempts >= maxAttempts)
        {
            Status = EmailStatus.Abandoned;
        }
        else
        {
            Status = EmailStatus.Pending;
            NextAttemptAt = now + retryDelay;
        }
    }

    /// <summary>Gives a claimed message back without counting the attempt (rate limit reached, shutdown).</summary>
    public void Release(DateTime notBefore)
    {
        if (Status != EmailStatus.Sending)
        {
            return;
        }

        Status = EmailStatus.Pending;
        NextAttemptAt = notBefore;
        LeaseToken = null;
        LeaseUntil = null;
        if (Attempts > 0)
        {
            Attempts--;
        }
    }

    /// <summary>Ends a message without sending it (e.g. the recipient is on the suppression list).</summary>
    public void Fail(string sanitisedError)
    {
        Status = EmailStatus.Failed;
        LastError = Truncate(sanitisedError, ErrorMaxLength);
        LeaseToken = null;
        LeaseUntil = null;
    }

    /// <summary>Administrator retry of a message that failed or was abandoned: a fresh set of attempts, due immediately.</summary>
    public void Retry(DateTime now)
    {
        if (Status is not (EmailStatus.Failed or EmailStatus.Abandoned))
        {
            throw new DomainException("EMAIL_NOT_RETRYABLE", "Only failed or abandoned messages can be retried.");
        }

        if (!HasBodies)
        {
            throw new DomainException("EMAIL_BODY_PURGED", "The message content is no longer stored, so it cannot be sent again.");
        }

        Status = EmailStatus.Pending;
        Attempts = 0;
        NextAttemptAt = now;
        LastError = null;
    }

    public void PurgeBodies(DateTime now)
    {
        TextBody = null;
        HtmlBody = null;
        BodiesPurgedAt = now;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>An address mail is no longer sent to, because the mail server permanently refused it (or an administrator added it).</summary>
public sealed class EmailSuppression : Entity
{
    public const int ReasonMaxLength = 300;

    private EmailSuppression()
    {
    }

    /// <summary>Lower-case address.</summary>
    public string Address { get; private set; } = string.Empty;

    public string Reason { get; private set; } = string.Empty;

    public Guid? SourceEmailId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public static string Normalize(string address) => address.Trim().ToLowerInvariant();

    public static EmailSuppression Create(string address, string reason, Guid? sourceEmailId, DateTime now) => new()
    {
        Address = Normalize(address),
        Reason = reason.Length <= ReasonMaxLength ? reason : reason[..ReasonMaxLength],
        SourceEmailId = sourceEmailId,
        CreatedAt = now,
    };
}
