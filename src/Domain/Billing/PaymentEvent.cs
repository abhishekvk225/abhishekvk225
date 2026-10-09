using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Billing;

/// <summary>
/// The idempotent ledger of provider notifications: a unique <c>(Provider, EventId)</c> means a redelivered event is recognised and
/// ignored. The row is written in the same transaction as the effect it caused, so an event whose processing failed leaves no trace and
/// is processed again when the provider retries. Global (not tenant-owned): the payload is not stored, only what is needed to audit it.
/// </summary>
public sealed class PaymentEvent : Entity
{
    private PaymentEvent()
    {
    }

    public string Provider { get; private set; } = string.Empty;

    public string EventId { get; private set; } = string.Empty;

    public string Type { get; private set; } = string.Empty;

    public Guid? OrderId { get; private set; }

    public DateTime ReceivedAt { get; private set; }

    public DateTime? ProcessedAt { get; private set; }

    public string? Outcome { get; private set; }

    public static PaymentEvent Receive(string provider, string eventId, string type, Guid? orderId, DateTime now) => new()
    {
        Provider = provider.ToLowerInvariant(),
        EventId = eventId.Length > 100 ? eventId[..100] : eventId,
        Type = type,
        OrderId = orderId,
        ReceivedAt = now,
    };

    public void Complete(string outcome, DateTime now)
    {
        Outcome = outcome.Length > 60 ? outcome[..60] : outcome;
        ProcessedAt = now;
    }
}
