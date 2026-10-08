using System.ComponentModel.DataAnnotations;
using System.Globalization;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Api;
using NexaVerify.Application.Common;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Dashboards;
using NexaVerify.Domain.Api;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.Licensing;

public sealed class LicenseAlertOptions
{
    public const string SectionName = "Metering:Alerts";

    /// <summary>Turns the hourly job off (tests drive the processor by hand).</summary>
    public bool Enabled { get; set; } = true;

    [Range(1, 1440)]
    public int IntervalMinutes { get; set; } = 60;

    [Range(0, 3600)]
    public int InitialDelaySeconds { get; set; } = 60;

    /// <summary>A license with this share of its credits left (or less) raises <c>license.low_balance</c>.</summary>
    [Range(1, 99)]
    public int LowBalancePercent { get; set; } = 10;

    /// <summary>First notice: <c>license.expiring</c> fires when a license is this many days from its end.</summary>
    [Range(1, 90)]
    public int ExpiringNoticeDays { get; set; } = 7;

    /// <summary>Final notice (must be shorter than the first).</summary>
    [Range(1, 90)]
    public int ExpiringFinalNoticeDays { get; set; } = 1;

    /// <summary>The notice thresholds, tightest first.</summary>
    public IEnumerable<int> ExpiringThresholds() => new[] { ExpiringFinalNoticeDays, ExpiringNoticeDays }.Where(d => d > 0).Distinct().Order();

    [Range(1, 90)]
    public int ApiKeyExpiringDays { get; set; } = 7;

    /// <summary>A license that ended longer ago than this no longer raises <c>license.expired</c> (stops a first run from announcing ancient history).</summary>
    [Range(1, 30)]
    public int ExpiredLookbackDays { get; set; } = 3;

    [Range(10, 5000)]
    public int BatchSize { get; set; } = 500;
}

public sealed record LicenseAlertCandidate(
    Guid LicenseId, Guid ClientId, string Name, LicenseStatus Status, int TotalCredits, int ConsumedCredits, DateTime StartsAt, DateTime ExpiresAt);

public sealed record ApiKeyAlertCandidate(Guid ApiKeyId, Guid ClientId, string Name, string KeyPrefix, DateTime ExpiresAt);

/// <summary>An alert that is due: what to store, what to tell the client, and the webhook event to publish. No personal data.</summary>
public sealed record DueAlert(
    LicenseAlertType Type, Guid SubjectId, string Bucket, AlertSeverity Severity, string EventType, string Title, string Message, object Payload);

/// <summary>The alert rules, pure and deterministic (all inputs are parameters): which alerts a license or key is due for, right now.</summary>
public static class LicenseAlertRules
{
    public static IReadOnlyList<DueAlert> Evaluate(LicenseAlertCandidate c, DateTime now, LicenseAlertOptions options)
    {
        var due = new List<DueAlert>();
        var remaining = c.TotalCredits - c.ConsumedCredits;
        var percent = c.TotalCredits <= 0 ? 0 : (int)Math.Floor(100.0 * remaining / c.TotalCredits);
        var endDate = c.ExpiresAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        object Payload(int? days = null) => new
        {
            licenseId = c.LicenseId,
            name = c.Name,
            remainingCredits = remaining,
            totalCredits = c.TotalCredits,
            percentRemaining = percent,
            expiresAt = c.ExpiresAt,
            daysRemaining = days,
        };

        var usable = c.Status == LicenseStatus.Active && c.StartsAt <= now && now < c.ExpiresAt;
        if (usable && c.TotalCredits > 0)
        {
            // The total is part of the bucket: topping a license up and running low again is a new crossing and may alert again.
            if (remaining == 0)
            {
                due.Add(new DueAlert(LicenseAlertType.Exhausted, c.LicenseId, $"exhausted@{c.TotalCredits}", AlertSeverity.Critical, WebhookEvents.LicenseExhausted,
                    "Credits used up", $"License \"{c.Name}\" has no credits left.", Payload()));
            }
            else if (remaining * 100L <= (long)options.LowBalancePercent * c.TotalCredits)
            {
                due.Add(new DueAlert(LicenseAlertType.LowBalance, c.LicenseId, $"low{options.LowBalancePercent}@{c.TotalCredits}", AlertSeverity.Warning, WebhookEvents.LicenseLowBalance,
                    "Credits running low", $"License \"{c.Name}\" has {remaining} of {c.TotalCredits} credits left ({percent}%).", Payload()));
            }
        }

        if (c.Status == LicenseStatus.Active && c.ExpiresAt > now)
        {
            // Only the tightest threshold that applies: a license 20 hours from its end does not also announce "7 days".
            foreach (var days in options.ExpiringThresholds())
            {
                if (c.ExpiresAt <= now.AddDays(days))
                {
                    var unit = days == 1 ? "day" : "days";
                    due.Add(new DueAlert(LicenseAlertType.Expiring, c.LicenseId, $"exp{days}d@{endDate}", days <= 1 ? AlertSeverity.Critical : AlertSeverity.Warning, WebhookEvents.LicenseExpiring,
                        "License expiring soon", $"License \"{c.Name}\" expires within {days} {unit}.", Payload(days)));
                    break;
                }
            }
        }

        if (c.Status is LicenseStatus.Active or LicenseStatus.Expired && c.ExpiresAt <= now && c.ExpiresAt >= now.AddDays(-options.ExpiredLookbackDays))
        {
            due.Add(new DueAlert(LicenseAlertType.Expired, c.LicenseId, $"expired@{endDate}", AlertSeverity.Critical, WebhookEvents.LicenseExpired,
                "License expired", $"License \"{c.Name}\" has expired.", Payload()));
        }

        return due;
    }

    public static IReadOnlyList<DueAlert> Evaluate(ApiKeyAlertCandidate k, DateTime now, LicenseAlertOptions options)
    {
        if (k.ExpiresAt <= now || k.ExpiresAt > now.AddDays(options.ApiKeyExpiringDays))
        {
            return [];
        }

        var bucket = $"exp{options.ApiKeyExpiringDays}d@{k.ExpiresAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
        return
        [
            new DueAlert(LicenseAlertType.ApiKeyExpiring, k.ApiKeyId, bucket, AlertSeverity.Warning, WebhookEvents.ApiKeyExpiring,
                "API key expiring soon", $"API key \"{k.Name}\" ({k.KeyPrefix}) expires within {options.ApiKeyExpiringDays} days.",
                new { apiKeyId = k.ApiKeyId, name = k.Name, keyPrefix = k.KeyPrefix, expiresAt = k.ExpiresAt }),
        ];
    }
}

/// <summary>Platform-scope worklists for the alert job: only rows that could possibly be due, so the job never scans every license.</summary>
public interface ILicenseAlertCandidates
{
    Task<IReadOnlyList<LicenseAlertCandidate>> ListLicensesAsync(DateTime now, LicenseAlertOptions options, Guid? afterLicenseId, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApiKeyAlertCandidate>> ListApiKeysAsync(DateTime now, LicenseAlertOptions options, Guid? afterKeyId, int take, CancellationToken cancellationToken);
}

public interface ILicenseAlertRepository
{
    /// <summary>Which of these subjects already have which (type, bucket) alerts: one query for the whole batch.</summary>
    Task<HashSet<(Guid SubjectId, LicenseAlertType Type, string Bucket)>> RaisedAsync(IReadOnlyCollection<Guid> subjectIds, CancellationToken cancellationToken);

    Task<LicenseAlert?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<(IReadOnlyList<LicenseAlert> Items, int Total)> ListAsync(bool unreadOnly, int skip, int take, CancellationToken cancellationToken);

    Task<int> CountUnreadAsync(CancellationToken cancellationToken);

    void Add(LicenseAlert alert);
}

public interface ILicenseAlertService
{
    /// <summary>
    /// Raises the alerts that are due for these items (all belonging to the CURRENT tenant): stores each alert and stages its webhook
    /// event in one transaction. An alert already raised for the same (subject, type, bucket) is skipped. Returns how many were raised.
    /// </summary>
    Task<int> RaiseDueAsync(IReadOnlyList<LicenseAlertCandidate> licenses, IReadOnlyList<ApiKeyAlertCandidate> apiKeys, DateTime now, CancellationToken cancellationToken);
}

public sealed class LicenseAlertService : ILicenseAlertService
{
    private readonly ILicenseAlertRepository _alerts;
    private readonly IWebhookPublisher _webhooks;
    private readonly IUnitOfWork _unitOfWork;
    private readonly Microsoft.Extensions.Options.IOptions<LicenseAlertOptions> _options;

    public LicenseAlertService(ILicenseAlertRepository alerts, IWebhookPublisher webhooks, IUnitOfWork unitOfWork, Microsoft.Extensions.Options.IOptions<LicenseAlertOptions> options)
    {
        _alerts = alerts;
        _webhooks = webhooks;
        _unitOfWork = unitOfWork;
        _options = options;
    }

    public async Task<int> RaiseDueAsync(
        IReadOnlyList<LicenseAlertCandidate> licenses, IReadOnlyList<ApiKeyAlertCandidate> apiKeys, DateTime now, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var raised = 0;
        var candidates = licenses.SelectMany(l => LicenseAlertRules.Evaluate(l, now, options).Select(a => (l.ClientId, Alert: a)))
            .Concat(apiKeys.SelectMany(k => LicenseAlertRules.Evaluate(k, now, options).Select(a => (k.ClientId, Alert: a))))
            .ToList();
        if (candidates.Count == 0)
        {
            return 0;
        }

        var already = await _alerts.RaisedAsync(candidates.Select(c => c.Alert.SubjectId).Distinct().ToList(), cancellationToken);
        foreach (var (clientId, due) in candidates)
        {
            if (already.Contains((due.SubjectId, due.Type, due.Bucket)))
            {
                continue;
            }

            try
            {
                await _unitOfWork.ExecuteInTransactionAsync(
                    async ct =>
                    {
                        _alerts.Add(LicenseAlert.Create(clientId, due.Type, due.SubjectId, due.Bucket, due.Severity, due.Title, due.Message, now));
                        await _webhooks.PublishAsync(clientId, due.EventType, due.Payload, ct);
                        await _unitOfWork.SaveChangesAsync(ct); // alert row + queued deliveries commit together or not at all
                        return true;
                    },
                    cancellationToken);
                raised++;
            }
            catch (UniqueConstraintViolationException)
            {
                _unitOfWork.ClearTracked(); // another node raised it first; its webhook rows are the ones that committed
            }
        }

        return raised;
    }
}

public interface INotificationService
{
    Task<Result<NotificationFeedDto>> ListAsync(NotificationListQuery query, CancellationToken cancellationToken);

    Task<Result> MarkReadAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>The in-app notification feed: the same alert rows the webhooks were raised from, for the calling client only.</summary>
public sealed class NotificationService : INotificationService
{
    private readonly ILicenseAlertRepository _alerts;
    private readonly ICurrentUser _currentUser;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;

    public NotificationService(ILicenseAlertRepository alerts, ICurrentUser currentUser, IUnitOfWork unitOfWork, TimeProvider time)
    {
        _alerts = alerts;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
        _time = time;
    }

    public async Task<Result<NotificationFeedDto>> ListAsync(NotificationListQuery query, CancellationToken cancellationToken)
    {
        if (_currentUser.ClientId is null)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only client accounts have notifications.");
        }

        var paging = new PageRequest { Page = query.Page, PageSize = query.PageSize }.Normalize();
        var (items, total) = await _alerts.ListAsync(query.UnreadOnly, paging.Skip, paging.PageSize, cancellationToken);
        var unread = await _alerts.CountUnreadAsync(cancellationToken);
        var page = new PagedResult<NotificationDto>(
            items.Select(a => new NotificationDto(a.Id, a.AlertType.ToString(), a.Severity.ToString(), a.Title, a.Message, a.CreatedAt, a.ReadAt is not null)).ToList(),
            paging.Page, paging.PageSize, total);
        return new NotificationFeedDto(unread, page);
    }

    public async Task<Result> MarkReadAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_currentUser.ClientId is null)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only client accounts have notifications.");
        }

        var alert = await _alerts.GetAsync(id, cancellationToken);
        if (alert is null)
        {
            return Error.NotFound();
        }

        alert.MarkRead(_time.GetUtcNow().UtcDateTime);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
