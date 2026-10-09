using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Billing;

public enum RefundStatus
{
    Pending = 0,
    Succeeded,
    Failed,
}

/// <summary>
/// Money given back for an order. The row is created (Pending) together with the reservation of the amount on the order and before the
/// provider is called; its id doubles as the provider's idempotency key, so a retry after a crash can never refund twice.
/// </summary>
public sealed class Refund : AuditableEntity, ITenantOwned
{
    public const int ReasonMaxLength = 500;

    private Refund()
    {
    }

    public Guid ClientId { get; set; }

    public Guid OrderId { get; private set; }

    public long AmountMinor { get; private set; }

    public int CreditsRevoked { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public RefundStatus Status { get; private set; }

    public string? ProviderRefundId { get; private set; }

    public DateTime? CompletedAt { get; private set; }

    public static Refund Start(Guid clientId, Guid orderId, long amountMinor, string reason)
    {
        if (amountMinor <= 0)
        {
            throw new DomainException("REFUND_AMOUNT_INVALID", "The refund amount must be positive.");
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > ReasonMaxLength)
        {
            throw new DomainException("REFUND_REASON_INVALID", "A reason of at most 500 characters is required.");
        }

        return new Refund { ClientId = clientId, OrderId = orderId, AmountMinor = amountMinor, Reason = reason.Trim(), Status = RefundStatus.Pending };
    }

    public void Succeed(string providerRefundId, int creditsRevoked, DateTime now)
    {
        if (Status != RefundStatus.Pending)
        {
            throw new DomainException("REFUND_INVALID_TRANSITION", $"A {Status} refund cannot succeed.");
        }

        Status = RefundStatus.Succeeded;
        ProviderRefundId = providerRefundId.Length > 100 ? providerRefundId[..100] : providerRefundId;
        CreditsRevoked = creditsRevoked;
        CompletedAt = now;
    }

    public void Fail(DateTime now)
    {
        if (Status != RefundStatus.Pending)
        {
            throw new DomainException("REFUND_INVALID_TRANSITION", $"A {Status} refund cannot fail.");
        }

        Status = RefundStatus.Failed;
        CompletedAt = now;
    }
}
