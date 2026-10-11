using FluentValidation;
using Microsoft.Extensions.Logging;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Messaging;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Messaging;

namespace NexaVerify.Application.Messaging;

/// <summary>Hides most of a mailbox name for people who may see that mail was sent but not to whom: <c>jane.doe@example.com</c> becomes <c>j***@example.com</c>.</summary>
public static class EmailAddressMasker
{
    public static string Mask(string address)
    {
        var at = address.LastIndexOf('@');
        return at <= 0 ? "***" : address[0] + "***" + address[at..];
    }
}

public sealed record EmailOutboxFilter(EmailStatus? Status, EmailTemplate? Template, string? Recipient, DateTime? From, DateTime? To);

/// <summary>An outbox row without its (large, possibly sensitive) bodies.</summary>
public sealed record EmailOutboxRow(
    Guid Id, string ToAddress, string Subject, EmailTemplate Template, EmailStatus Status, int Attempts, DateTime NextAttemptAt,
    DateTime? LastAttemptAt, string? LastError, string CorrelationId, DateTime CreatedAt, DateTime? SentAt)
{
    public static EmailOutboxRow From(OutboundEmail e) => new(
        e.Id, e.ToAddress, e.Subject, e.Template, e.Status, e.Attempts, e.NextAttemptAt, e.LastAttemptAt, e.LastError, e.CorrelationId, e.CreatedAt, e.SentAt);
}

public interface IEmailOutboxRepository
{
    Task<(IReadOnlyList<EmailOutboxRow> Items, int Total)> ListAsync(EmailOutboxFilter filter, int skip, int take, CancellationToken cancellationToken);

    Task<OutboundEmail?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<(IReadOnlyList<EmailSuppression> Items, int Total)> ListSuppressionsAsync(int skip, int take, CancellationToken cancellationToken);

    Task<EmailSuppression?> GetSuppressionAsync(Guid id, CancellationToken cancellationToken);

    void Remove(EmailSuppression suppression);
}

public interface IEmailAdminService
{
    Task<Result<PagedResult<EmailOutboxItemDto>>> ListOutboxAsync(EmailOutboxQuery query, CancellationToken cancellationToken);

    Task<Result<EmailOutboxItemDto>> RetryAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<PagedResult<EmailSuppressionDto>>> ListSuppressionsAsync(PageRequest page, CancellationToken cancellationToken);

    Task<Result> RemoveSuppressionAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<SendTestEmailResponse>> SendTestAsync(SendTestEmailRequest request, CancellationToken cancellationToken);
}

public static class EmailAuditActions
{
    public const string Retried = "email.retried";
    public const string SuppressionRemoved = "email.suppression_removed";
    public const string TestSent = "email.test_sent";
}

public sealed class SendTestEmailRequestValidator : AbstractValidator<SendTestEmailRequest>
{
    public SendTestEmailRequestValidator()
    {
        RuleFor(x => x.To).NotEmpty().MaximumLength(OutboundEmail.AddressMaxLength).EmailAddress()
            .Must(a => !a.Any(char.IsControl) && !a.Contains(' ', StringComparison.Ordinal)).WithMessage("'To' must be a plain email address.");
    }
}

public sealed partial class EmailAdminService : IEmailAdminService
{
    private readonly IEmailOutboxRepository _outbox;
    private readonly IEmailSender _sender;
    private readonly IPermissionResolver _permissions;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;
    private readonly ILogger<EmailAdminService> _logger;

    public EmailAdminService(
        IEmailOutboxRepository outbox,
        IEmailSender sender,
        IPermissionResolver permissions,
        ICurrentUser currentUser,
        IAuditService audit,
        IUnitOfWork unitOfWork,
        TimeProvider time,
        ILogger<EmailAdminService> logger)
    {
        _outbox = outbox;
        _sender = sender;
        _permissions = permissions;
        _currentUser = currentUser;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _time = time;
        _logger = logger;
    }

    public async Task<Result<PagedResult<EmailOutboxItemDto>>> ListOutboxAsync(EmailOutboxQuery query, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        EmailStatus? status = null;
        EmailTemplate? template = null;
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (Enum.TryParse<EmailStatus>(query.Status, ignoreCase: true, out var s) && Enum.IsDefined(s))
            {
                status = s;
            }
            else
            {
                errors["status"] = [$"Unknown status. Use one of: {string.Join(", ", Enum.GetNames<EmailStatus>())}."];
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Template))
        {
            if (Enum.TryParse<EmailTemplate>(query.Template, ignoreCase: true, out var t) && Enum.IsDefined(t))
            {
                template = t;
            }
            else
            {
                errors["template"] = ["Unknown template."];
            }
        }

        if (query.From is { } from && query.To is { } to && from > to)
        {
            errors["from"] = ["'from' must not be after 'to'."];
        }

        if (query.Recipient is { Length: > OutboundEmail.AddressMaxLength })
        {
            errors["recipient"] = ["The recipient filter is too long."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The filter is not valid.", errors);
        }

        var paging = new PageRequest { Page = query.Page, PageSize = query.PageSize }.Normalize();
        var recipient = string.IsNullOrWhiteSpace(query.Recipient) ? null : query.Recipient.Trim().ToLowerInvariant();
        var (items, total) = await _outbox.ListAsync(new EmailOutboxFilter(status, template, recipient, query.From, query.To), paging.Skip, paging.PageSize, cancellationToken);
        var canSee = await CanSeeAddressesAsync(cancellationToken);
        return new PagedResult<EmailOutboxItemDto>([.. items.Select(e => ToDto(e, canSee))], paging.Page, paging.PageSize, total);
    }

    public async Task<Result<EmailOutboxItemDto>> RetryAsync(Guid id, CancellationToken cancellationToken)
    {
        var email = await _outbox.GetAsync(id, cancellationToken);
        if (email is null)
        {
            return Error.NotFound();
        }

        try
        {
            email.Retry(_time.GetUtcNow().UtcDateTime);
        }
        catch (DomainException ex)
        {
            return Error.Conflict(ex.Code, ex.Message);
        }

        _audit.Record(new AuditEntry(EmailAuditActions.Retried, nameof(OutboundEmail), email.Id.ToString(), ActorId: _currentUser.ActorId,
            NewValues: new { Template = email.Template.ToString(), To = EmailAddressMasker.Mask(email.ToAddress) }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(EmailOutboxRow.From(email), await CanSeeAddressesAsync(cancellationToken));
    }

    public async Task<Result<PagedResult<EmailSuppressionDto>>> ListSuppressionsAsync(PageRequest page, CancellationToken cancellationToken)
    {
        var paging = page.Normalize();
        var (items, total) = await _outbox.ListSuppressionsAsync(paging.Skip, paging.PageSize, cancellationToken);
        return new PagedResult<EmailSuppressionDto>([.. items.Select(s => new EmailSuppressionDto(s.Id, s.Address, s.Reason, s.CreatedAt))], paging.Page, paging.PageSize, total);
    }

    public async Task<Result> RemoveSuppressionAsync(Guid id, CancellationToken cancellationToken)
    {
        var suppression = await _outbox.GetSuppressionAsync(id, cancellationToken);
        if (suppression is null)
        {
            return Error.NotFound();
        }

        _outbox.Remove(suppression);
        _audit.Record(new AuditEntry(EmailAuditActions.SuppressionRemoved, nameof(EmailSuppression), suppression.Id.ToString(), ActorId: _currentUser.ActorId,
            OldValues: new { Address = EmailAddressMasker.Mask(suppression.Address) }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<SendTestEmailResponse>> SendTestAsync(SendTestEmailRequest request, CancellationToken cancellationToken)
    {
        var to = request.To.Trim();
        var id = await _sender.SendAsync(
            new EmailRequest(EmailTemplate.AdminTest, to).Text("requestedBy", _currentUser.ActorId?.ToString("D") ?? "an administrator"), cancellationToken);
        if (id is not { } queued)
        {
            return Error.Validation("The address cannot be used.", new Dictionary<string, string[]> { ["to"] = ["The address cannot be used."] });
        }

        _audit.Record(new AuditEntry(EmailAuditActions.TestSent, nameof(OutboundEmail), queued.ToString(), ActorId: _currentUser.ActorId,
            NewValues: new { To = EmailAddressMasker.Mask(to) }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        LogTestQueued(queued);
        return new SendTestEmailResponse(queued);
    }

    private async Task<bool> CanSeeAddressesAsync(CancellationToken cancellationToken) =>
        (await _permissions.GetPermissionsAsync(_currentUser.Roles, cancellationToken)).Contains(Permissions.Email.Read);

    private static EmailOutboxItemDto ToDto(EmailOutboxRow e, bool showAddress) => new(
        e.Id, showAddress ? e.ToAddress : EmailAddressMasker.Mask(e.ToAddress), e.Subject, e.Template.ToString(), e.Status.ToString(), e.Attempts,
        e.NextAttemptAt, e.LastAttemptAt, e.LastError, e.CorrelationId, e.CreatedAt, e.SentAt);

    [LoggerMessage(EventId = 8210, Level = LogLevel.Information, Message = "Test email {EmailId} queued")]
    private partial void LogTestQueued(Guid emailId);
}
