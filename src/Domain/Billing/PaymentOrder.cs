using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Billing;

public enum PaymentOrderStatus
{
    Pending = 0,
    Paid,
    Failed,
    Expired,
    Cancelled,
    Refunded,
    PartiallyRefunded,
}

/// <summary>
/// One purchase of a credit pack. Everything the customer was shown (pack name, credits, price, tax) is copied into the order, so the
/// invoice and the amount we expect from the payment provider never depend on later edits of the pack. State changes that races could
/// corrupt (paid, refund reservations) are single conditional UPDATEs in persistence; the sets below are the one definition of which
/// states they may start from.
/// </summary>
public sealed class PaymentOrder : AuditableEntity, ITenantOwned
{
    public const int IdempotencyKeyMaxLength = 100;

    private PaymentOrder()
    {
    }

    public Guid ClientId { get; set; }

    public Guid PackId { get; private set; }

    public string PackName { get; private set; } = string.Empty;

    public int Credits { get; private set; }

    public int ValidityDays { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public long SubtotalMinor { get; private set; }

    public long TaxMinor { get; private set; }

    public long TotalMinor { get; private set; }

    public decimal TaxPercent { get; private set; }

    public string TaxLabel { get; private set; } = string.Empty;

    public PaymentOrderStatus Status { get; private set; }

    /// <summary>Lower-case provider name the checkout was created with (<c>stripe</c>, <c>razorpay</c>, <c>simulated</c>).</summary>
    public string Provider { get; private set; } = string.Empty;

    public string? ProviderSessionId { get; private set; }

    public string? ProviderPaymentId { get; private set; }

    /// <summary>The hosted payment page. Kept so a retried request with the same idempotency key gets the same page back.</summary>
    public string? CheckoutUrl { get; private set; }

    public DateTime ExpiresAt { get; private set; }

    public DateTime? PaidAt { get; private set; }

    public Guid? LicenseId { get; private set; }

    public string? InvoiceNumber { get; private set; }

    public string? IdempotencyKey { get; private set; }

    /// <summary>JSON copy of the billing profile at checkout time (the invoice's "bill to").</summary>
    public string BuyerJson { get; private set; } = "{}";

    public long RefundedMinor { get; private set; }

    public int CreditsRevoked { get; private set; }

    public DateTime? LastCheckedAt { get; private set; }

    public string? FailureReason { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>A verified payment is accepted for an order in one of these states (Failed: providers let the customer retry in the same session).</summary>
    public static IReadOnlyList<PaymentOrderStatus> PayableStatuses { get; } = [PaymentOrderStatus.Pending, PaymentOrderStatus.Expired, PaymentOrderStatus.Failed];

    public static IReadOnlyList<PaymentOrderStatus> RefundableStatuses { get; } = [PaymentOrderStatus.Paid, PaymentOrderStatus.PartiallyRefunded];

    public bool IsPayable => PayableStatuses.Contains(Status);

    public bool IsRefundable => RefundableStatuses.Contains(Status);

    public long RefundableMinor => TotalMinor - RefundedMinor;

    /// <summary>Tax on <paramref name="subtotalMinor"/>, rounded half away from zero to the minor unit.</summary>
    public static long TaxFor(long subtotalMinor, decimal percent) =>
        (long)Math.Round(subtotalMinor * percent / 100m, 0, MidpointRounding.AwayFromZero);

    public static PaymentOrder Create(
        Guid clientId, CreditPack pack, decimal taxPercent, string taxLabel, string provider, string? idempotencyKey,
        string buyerJson, DateTime expiresAt)
    {
        if (taxPercent is < 0 or > 100)
        {
            throw new DomainException("ORDER_TAX_INVALID", "The tax rate must be between 0 and 100.");
        }

        var tax = TaxFor(pack.PriceMinor, taxPercent);
        return new PaymentOrder
        {
            ClientId = clientId,
            PackId = pack.Id,
            PackName = pack.Name,
            Credits = pack.Credits,
            ValidityDays = pack.ValidityDays,
            Currency = pack.Currency,
            SubtotalMinor = pack.PriceMinor,
            TaxMinor = tax,
            TotalMinor = pack.PriceMinor + tax,
            TaxPercent = taxPercent,
            TaxLabel = taxLabel,
            Status = PaymentOrderStatus.Pending,
            Provider = provider.ToLowerInvariant(),
            IdempotencyKey = idempotencyKey,
            BuyerJson = buyerJson,
            ExpiresAt = expiresAt,
        };
    }

    public void AttachCheckout(string sessionId, string checkoutUrl, DateTime expiresAt)
    {
        ProviderSessionId = sessionId;
        CheckoutUrl = checkoutUrl;
        ExpiresAt = expiresAt;
    }

    /// <summary>The provider could not create the hosted page: the order never became payable.</summary>
    public void FailCheckout(string reason)
    {
        if (Status != PaymentOrderStatus.Pending)
        {
            throw new DomainException("ORDER_INVALID_TRANSITION", $"A {Status} order cannot fail at checkout.");
        }

        Status = PaymentOrderStatus.Failed;
        FailureReason = reason.Length > 200 ? reason[..200] : reason;
    }
}
