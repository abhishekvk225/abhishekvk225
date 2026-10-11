using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Messaging;
using NexaVerify.Domain.Messaging;

namespace NexaVerify.Infrastructure.Persistence.Repositories;

internal sealed class EmailOutboxRepository : IEmailOutboxRepository
{
    private readonly AppDbContext _db;

    public EmailOutboxRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<(IReadOnlyList<EmailOutboxRow> Items, int Total)> ListAsync(EmailOutboxFilter filter, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _db.OutboundEmails.AsNoTracking();
        if (filter.Status is { } status)
        {
            query = query.Where(e => e.Status == status);
        }

        if (filter.Template is { } template)
        {
            query = query.Where(e => e.Template == template);
        }

        if (!string.IsNullOrEmpty(filter.Recipient))
        {
            var recipient = filter.Recipient;
            query = recipient.Contains('@', StringComparison.Ordinal)
                ? query.Where(e => e.ToAddress == recipient)
                : query.Where(e => e.ToAddress.StartsWith(recipient));
        }

        if (filter.From is { } from)
        {
            query = query.Where(e => e.CreatedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(e => e.CreatedAt <= to);
        }

        var total = await query.CountAsync(cancellationToken);

        // Bodies are never loaded: they are large and may still hold a one-time link.
        var items = await query.OrderByDescending(e => e.CreatedAt).ThenBy(e => e.Id).Skip(skip).Take(take)
            .Select(e => new EmailOutboxRow(
                e.Id, e.ToAddress, e.Subject, e.Template, e.Status, e.Attempts, e.NextAttemptAt, e.LastAttemptAt, e.LastError, e.CorrelationId, e.CreatedAt, e.SentAt))
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<OutboundEmail?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _db.OutboundEmails.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<EmailSuppression> Items, int Total)> ListSuppressionsAsync(int skip, int take, CancellationToken cancellationToken)
    {
        var query = _db.EmailSuppressions.AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<EmailSuppression?> GetSuppressionAsync(Guid id, CancellationToken cancellationToken) =>
        _db.EmailSuppressions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public void Remove(EmailSuppression suppression) => _db.EmailSuppressions.Remove(suppression);
}
