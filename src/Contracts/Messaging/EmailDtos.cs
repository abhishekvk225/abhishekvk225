namespace NexaVerify.Contracts.Messaging;

/// <summary>One message in the email outbox. <c>to</c> is masked (<c>a***@example.com</c>) unless the caller holds <c>email.read</c>. Bodies are never returned.</summary>
public sealed record EmailOutboxItemDto(
    Guid Id,
    string To,
    string Subject,
    string Template,
    string Status,
    int Attempts,
    DateTime NextAttemptAt,
    DateTime? LastAttemptAt,
    string? LastError,
    string CorrelationId,
    DateTime CreatedAt,
    DateTime? SentAt);

/// <summary>Filters of <c>GET /admin/email/outbox</c>. Dates are UTC.</summary>
public sealed record EmailOutboxQuery
{
    public string? Status { get; init; }

    public string? Template { get; init; }

    public string? Recipient { get; init; }

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;
}

public sealed record EmailSuppressionDto(Guid Id, string Address, string Reason, DateTime CreatedAt);

public sealed record SendTestEmailRequest(string To);

public sealed record SendTestEmailResponse(Guid Id);
