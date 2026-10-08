using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Api;

public enum WebhookStatus
{
    Active = 0,
    Disabled,
}

public enum DeliveryStatus
{
    Pending = 0,
    Delivered,
    Abandoned,
}

public static class WebhookEvents
{
    public const string RecognitionCompleted = "recognition.completed";
    public const string LicenseLowBalance = "license.low_balance";
    public const string LicenseExpiring = "license.expiring";
    public const string LicenseExpired = "license.expired";
    public const string LicenseExhausted = "license.exhausted";
    public const string ApiKeyExpiring = "apikey.expiring";

    /// <summary>Sent by "send test event"; always deliverable to the endpoint it targets.</summary>
    public const string Test = "webhook.test";

    public static IReadOnlyList<string> Subscribable { get; } =
        [RecognitionCompleted, LicenseLowBalance, LicenseExpiring, LicenseExpired, LicenseExhausted, ApiKeyExpiring];
}

/// <summary>A URL a client wants events POSTed to. The signing secret is stored encrypted and shown once.</summary>
public sealed class WebhookEndpoint : AuditableEntity, ITenantOwned
{
    public const int MaxPerClient = 10;
    public const int DisableAfterConsecutiveFailures = 20;

    private WebhookEndpoint()
    {
    }

    public Guid ClientId { get; set; }

    public string Name { get; private set; } = string.Empty;

    public string Url { get; private set; } = string.Empty;

    public byte[] SecretEnc { get; private set; } = [];

    /// <summary>Comma-separated subscribed event types.</summary>
    public string Events { get; private set; } = string.Empty;

    public WebhookStatus Status { get; private set; }

    public int FailureCount { get; private set; }

    public DateTime? DisabledAt { get; private set; }

    public string? DisabledReason { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<string> EventList => Events.Length == 0 ? [] : Events.Split(',');

    public static WebhookEndpoint Create(Guid clientId, string name, string url, IReadOnlyCollection<string> events, byte[] secretEnc)
    {
        var endpoint = new WebhookEndpoint { ClientId = clientId, SecretEnc = secretEnc, Status = WebhookStatus.Active };
        endpoint.Apply(name, url, events);
        return endpoint;
    }

    public void Update(string name, string url, IReadOnlyCollection<string> events) => Apply(name, url, events);

    public void ReplaceSecret(byte[] secretEnc) => SecretEnc = secretEnc;

    public void Enable()
    {
        Status = WebhookStatus.Active;
        FailureCount = 0;
        DisabledAt = null;
        DisabledReason = null;
    }

    public void Disable(string reason, DateTime now)
    {
        Status = WebhookStatus.Disabled;
        DisabledAt = now;
        DisabledReason = reason.Length > 300 ? reason[..300] : reason;
    }

    public bool Subscribes(string eventType) => EventList.Contains(eventType, StringComparer.Ordinal);

    private void Apply(string name, string url, IReadOnlyCollection<string> events)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > 100)
        {
            throw new DomainException("WEBHOOK_NAME_INVALID", "Name must be 1–100 characters.");
        }

        if (url is null || url.Length is 0 or > 500)
        {
            throw new DomainException("WEBHOOK_URL_INVALID", "The URL must be 1–500 characters.");
        }

        var distinct = events.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0 || distinct.Any(e => !WebhookEvents.Subscribable.Contains(e)))
        {
            throw new DomainException("WEBHOOK_EVENTS_INVALID", "Choose at least one supported event.");
        }

        Name = trimmed;
        Url = url.Trim();
        Events = string.Join(',', distinct.Order(StringComparer.Ordinal));
    }
}

/// <summary>One event queued for one endpoint (a transactional outbox row, written in the same transaction as the business change).</summary>
public sealed class WebhookDelivery : ITenantOwned
{
    public const int MaxAttempts = 8;

    private WebhookDelivery()
    {
    }

    public long Id { get; private set; }

    public Guid ClientId { get; set; }

    public Guid EndpointId { get; private set; }

    /// <summary>Stable across retries so the receiver can de-duplicate (sent as X-Event-Id).</summary>
    public Guid EventId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public string PayloadJson { get; private set; } = string.Empty;

    public DeliveryStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTime NextAttemptAt { get; private set; }

    public int? LastStatusCode { get; private set; }

    public string? LastError { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? DeliveredAt { get; private set; }

    public static WebhookDelivery Queue(Guid clientId, Guid endpointId, Guid eventId, string eventType, string payloadJson, DateTime now) =>
        new()
        {
            ClientId = clientId,
            EndpointId = endpointId,
            EventId = eventId,
            EventType = eventType,
            PayloadJson = payloadJson,
            Status = DeliveryStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
        };

    public void MarkDelivered(int statusCode, DateTime now)
    {
        Attempts++;
        Status = DeliveryStatus.Delivered;
        LastStatusCode = statusCode;
        LastError = null;
        DeliveredAt = now;
    }

    /// <summary>Records a failed attempt; returns true when the delivery is now abandoned.</summary>
    public bool MarkFailed(int? statusCode, string error, DateTime now)
    {
        Attempts++;
        LastStatusCode = statusCode;
        LastError = error.Length > 300 ? error[..300] : error;
        if (Attempts >= MaxAttempts)
        {
            Status = DeliveryStatus.Abandoned;
            return true;
        }

        // 30 s, 1 m, 2 m, 4 m, 8 m, 16 m, 32 m ... with ±10 % jitter so a recovering receiver is not hit in lockstep.
        var seconds = 30 * Math.Pow(2, Attempts - 1);
        var jitter = 1 + ((Random.Shared.NextDouble() - 0.5) * 0.2);
        NextAttemptAt = now.AddSeconds(Math.Min(seconds * jitter, 6 * 3600));
        return false;
    }

    /// <summary>Gives up without sending (the endpoint is disabled or gone).</summary>
    public void Abandon(string reason, DateTime now)
    {
        Status = DeliveryStatus.Abandoned;
        LastError = reason.Length > 300 ? reason[..300] : reason;
        _ = now;
    }

    /// <summary>Re-queues an abandoned delivery for one more round (manual retry).</summary>
    public void Requeue(DateTime now)
    {
        Status = DeliveryStatus.Pending;
        Attempts = 0;
        NextAttemptAt = now;
    }
}
