using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Common;
using NexaVerify.Domain.Messaging;
using NexaVerify.Infrastructure.Messaging;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.Infrastructure.Platform;

namespace NexaVerify.Infrastructure.Background;

/// <summary>
/// Delivers the email outbox. Several nodes can run it: a message is claimed by one statement (<c>UPDLOCK, READPAST</c>) under a lease, so it
/// is attempted by one node at a time and a crashed node's messages come back after the lease. Permanent failures (a refused recipient) end
/// the message and suppress the address; transient ones retry with exponential backoff and jitter until the attempts are used up and the
/// message is abandoned (a Critical log and visible in the admin outbox). Delivery is at-least-once: a crash between the mail server
/// accepting a message and the status being saved can send it twice, which the fixed Message-ID makes recognisable.
/// </summary>
public sealed partial class EmailDispatcher : BackgroundService
{
    private static readonly TimeSpan TokenBodyRetention = TimeSpan.FromDays(7);

    private readonly IServiceScopeFactory _scopes;
    private readonly IEmailTransport _transport;
    private readonly EmailOptions _options;
    private readonly ILogger<EmailDispatcher> _logger;
    private readonly TimeProvider _time;
    private readonly Func<double> _random;
    private readonly TimeSpan _lease;
    private DateTime _lastPurge = DateTime.MinValue;

    public EmailDispatcher(
        IServiceScopeFactory scopes, IEmailTransport transport, IOptions<EmailOptions> options, ILogger<EmailDispatcher> logger, TimeProvider time,
        Func<double>? random = null)
    {
        _scopes = scopes;
        _transport = transport;
        _options = options.Value;
        _logger = logger;
        _time = time;
        _random = random ?? Random.Shared.NextDouble;
        _lease = EmailOptionsValidator.ResolveLease(_options);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Dispatcher.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.Dispatcher.PollSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await DispatchDueAsync(stoppingToken);
                    await PurgeAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogCycleFailed(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown: in-flight messages were handed back by DispatchDueAsync
        }
    }

    /// <summary>Claims and delivers everything currently due (within the per-minute budget); returns how many messages were attempted. Public for tests.</summary>
    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var token = Guid.NewGuid();
        IReadOnlyList<Guid> ids;
        await using (var scope = _scopes.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<EmailStore>();
            var budget = _options.MaxPerMinute - await store.CountAttemptsSinceAsync(now.AddMinutes(-1), cancellationToken);
            var take = Math.Min(_options.Dispatcher.BatchSize, budget);
            if (take <= 0)
            {
                LogRateLimited(_options.MaxPerMinute);
                return 0;
            }

            ids = await store.ClaimDueAsync(take, _options.Dispatcher.MaxPerDomainPerCycle, now, _lease, token, cancellationToken);
        }

        if (ids.Count == 0)
        {
            return 0;
        }

        try
        {
            await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = _options.Dispatcher.Parallelism, CancellationToken = cancellationToken },
                async (id, ct) =>
                {
                    try
                    {
                        await DeliverAsync(id, token, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LogUnexpected(id, ex); // the lease expires and the message is tried again (the attempt was already counted)
                    }
                });
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Shutdown: whatever this node had claimed but not finished goes straight back, uncounted.
                await using var scope = _scopes.CreateAsyncScope();
                var released = await scope.ServiceProvider.GetRequiredService<EmailStore>()
                    .ReleaseHeldAsync(token, _time.GetUtcNow().UtcDateTime, CancellationToken.None);
                if (released > 0)
                {
                    LogReleased(released);
                }
            }
        }

        return ids.Count;
    }

    private async Task DeliverAsync(Guid id, Guid token, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mail = await db.OutboundEmails.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (mail is null || mail.Status != EmailStatus.Sending || mail.LeaseToken != token)
        {
            return; // taken over by another node, or already handled
        }

        var dispatcher = _options.Dispatcher;
        var now = _time.GetUtcNow().UtcDateTime;
        if (mail.Attempts > dispatcher.MaxAttempts)
        {
            // Claimed again and again but never finished (a node keeps dying on it): stop here.
            mail.RecordFailure(permanent: false, "Gave up: delivery was interrupted repeatedly.", now, dispatcher.MaxAttempts, TimeSpan.Zero);
            await SaveAsync(db, mail, cancellationToken);
            LogAbandoned(mail.Id, mail.Template.ToString(), mail.Attempts, mail.LastError ?? string.Empty);
            return;
        }

        var normalized = EmailSuppression.Normalize(mail.ToAddress);
        if (await db.EmailSuppressions.AnyAsync(s => s.Address == normalized, cancellationToken))
        {
            mail.Fail("The recipient address is on the suppression list.");
            await SaveAsync(db, mail, cancellationToken);
            LogSuppressedSkip(mail.Id, mail.Template.ToString());
            return;
        }

        if (!mail.HasBodies)
        {
            mail.Fail("The message content is no longer stored.");
            await SaveAsync(db, mail, cancellationToken);
            return;
        }

        var outgoing = new OutgoingEmail(
            mail.Id, mail.ToAddress, mail.ToName, mail.Subject, mail.TextBody ?? string.Empty, mail.HtmlBody ?? string.Empty, mail.MessageId,
            mail.CorrelationId, mail.CreatedAt, mail.Template);

        EmailSendException? failure = null;
        try
        {
            await _transport.SendAsync(outgoing, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // shutdown; the claim is handed back by the caller
        }
        catch (EmailSendException ex)
        {
            failure = ex;
        }
        catch (Exception ex)
        {
            failure = new EmailSendException(EmailFailureKind.Transient, EmailText.Error($"Unexpected delivery error ({ex.GetType().Name}).", OutboundEmail.ErrorMaxLength), ex);
        }

        var finished = _time.GetUtcNow().UtcDateTime;
        if (failure is null)
        {
            mail.MarkSent(finished);
            await SaveAsync(db, mail, cancellationToken);
            LogSent(mail.Id, mail.Template.ToString(), mail.Attempts);
            return;
        }

        var error = EmailText.Error(failure.Message, OutboundEmail.ErrorMaxLength);
        mail.RecordFailure(failure.IsPermanent, error, finished, dispatcher.MaxAttempts, EmailBackoff.Delay(mail.Attempts, dispatcher, _random()));
        if (failure.Kind == EmailFailureKind.PermanentRecipient)
        {
            db.EmailSuppressions.Add(EmailSuppression.Create(mail.ToAddress, error, mail.Id, finished));
        }

        try
        {
            await SaveAsync(db, mail, cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            // The address was suppressed by a concurrent message: record the outcome of this one without the duplicate suppression.
            db.ChangeTracker.Clear();
            var again = await db.OutboundEmails.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
            if (again is not null && again.LeaseToken == token)
            {
                again.RecordFailure(failure.IsPermanent, error, finished, dispatcher.MaxAttempts, EmailBackoff.Delay(again.Attempts, dispatcher, _random()));
                await SaveAsync(db, again, cancellationToken);
            }
        }

        switch (mail.Status)
        {
            case EmailStatus.Pending:
                LogRetryScheduled(mail.Id, mail.Template.ToString(), mail.Attempts, dispatcher.MaxAttempts, mail.NextAttemptAt, error);
                break;
            case EmailStatus.Abandoned:
                LogAbandoned(mail.Id, mail.Template.ToString(), mail.Attempts, error);
                break;
            default:
                LogRefused(mail.Id, mail.Template.ToString(), failure.Kind.ToString(), error);
                break;
        }
    }

    private async Task SaveAsync(AppDbContext db, OutboundEmail mail, CancellationToken cancellationToken)
    {
        try
        {
            // Not cancelled by shutdown: the outcome of a send that already happened must be recorded.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (ConcurrencyConflictException)
        {
            LogLeaseLost(mail.Id);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Housekeeping, at most hourly: bodies of failed one-time-link mails are erased once their links are long dead, and finished messages
    /// are deleted after the retention period.
    /// </summary>
    public async Task PurgeAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (now - _lastPurge < TimeSpan.FromHours(1))
        {
            return;
        }

        _lastPurge = now;
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tokenCutoff = now - TokenBodyRetention;
        var erased = await db.OutboundEmails
            .Where(e => e.HasOneTimeToken && e.BodiesPurgedAt == null && (e.Status == EmailStatus.Failed || e.Status == EmailStatus.Abandoned) && e.CreatedAt < tokenCutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.TextBody, (string?)null).SetProperty(e => e.HtmlBody, (string?)null).SetProperty(e => e.BodiesPurgedAt, now), cancellationToken);

        var cutoff = now.AddDays(-_options.Dispatcher.RetentionDays);
        var removed = 0;
        int batch;
        do
        {
            batch = await db.OutboundEmails
                .Where(e => (e.Status == EmailStatus.Sent || e.Status == EmailStatus.Failed || e.Status == EmailStatus.Abandoned) && e.CreatedAt < cutoff)
                .OrderBy(e => e.Id).Take(1000)
                .ExecuteDeleteAsync(cancellationToken);
            removed += batch;
        }
        while (batch == 1000);

        if (erased > 0 || removed > 0)
        {
            LogPurged(erased, removed);
        }
    }

    [LoggerMessage(EventId = 8320, Level = LogLevel.Information, Message = "Email {EmailId} ({Template}) delivered on attempt {Attempt}")]
    private partial void LogSent(Guid emailId, string template, int attempt);

    [LoggerMessage(EventId = 8321, Level = LogLevel.Warning, Message = "Email {EmailId} ({Template}) failed on attempt {Attempt} of {MaxAttempts}; next try at {NextAttemptAt:u}: {Error}")]
    private partial void LogRetryScheduled(Guid emailId, string template, int attempt, int maxAttempts, DateTime nextAttemptAt, string error);

    [LoggerMessage(EventId = 8322, Level = LogLevel.Warning, Message = "Email {EmailId} ({Template}) was refused permanently ({Kind}) and will not be retried: {Error}")]
    private partial void LogRefused(Guid emailId, string template, string kind, string error);

    [LoggerMessage(EventId = 8323, Level = LogLevel.Critical, Message = "EMAIL ABANDONED: {EmailId} ({Template}) could not be delivered after {Attempts} attempts and will not be retried automatically. Check the mail server settings, then retry it from the admin email outbox. Last error: {Error}")]
    private partial void LogAbandoned(Guid emailId, string template, int attempts, string error);

    [LoggerMessage(EventId = 8324, Level = LogLevel.Information, Message = "Email {EmailId} ({Template}) skipped: the recipient is on the suppression list")]
    private partial void LogSuppressedSkip(Guid emailId, string template);

    [LoggerMessage(EventId = 8325, Level = LogLevel.Information, Message = "Email sending paused for now: the limit of {MaxPerMinute} per minute is reached")]
    private partial void LogRateLimited(int maxPerMinute);

    [LoggerMessage(EventId = 8326, Level = LogLevel.Warning, Message = "Email {EmailId} lost its lease to another node; that node's result stands")]
    private partial void LogLeaseLost(Guid emailId);

    [LoggerMessage(EventId = 8327, Level = LogLevel.Error, Message = "Email dispatch cycle failed")]
    private partial void LogCycleFailed(Exception ex);

    [LoggerMessage(EventId = 8328, Level = LogLevel.Error, Message = "Email {EmailId} failed unexpectedly; it will be retried when its lease expires")]
    private partial void LogUnexpected(Guid emailId, Exception ex);

    [LoggerMessage(EventId = 8329, Level = LogLevel.Information, Message = "Email dispatcher is stopping: {Count} claimed messages were handed back")]
    private partial void LogReleased(int count);

    [LoggerMessage(EventId = 8330, Level = LogLevel.Information, Message = "Email housekeeping: {Erased} one-time-link bodies erased, {Removed} finished messages deleted")]
    private partial void LogPurged(int erased, int removed);
}
