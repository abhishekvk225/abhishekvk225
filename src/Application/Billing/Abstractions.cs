using NexaVerify.Domain.Billing;

namespace NexaVerify.Application.Billing;

// ---- The payment provider boundary --------------------------------------------------------------------------------------------

public enum PaymentEventType
{
    PaymentSucceeded = 0,
    PaymentFailed,
    CheckoutExpired,
    RefundCreated,
}

/// <summary>A provider notification reduced to what the platform acts on. Nothing in it is trusted until the signature was verified.</summary>
public sealed record NormalisedPaymentEvent(
    string Provider,
    string EventId,
    PaymentEventType Type,
    Guid? OrderId,
    string? ProviderPaymentId,
    long? AmountMinor,
    string? Currency,
    string? ProviderRefundId = null);

public enum WebhookParseStatus
{
    /// <summary>Signature valid and the event is one we act on.</summary>
    Parsed = 0,

    /// <summary>Signature missing, wrong, or (Stripe) the timestamp is too old or too far ahead.</summary>
    InvalidSignature,

    /// <summary>Signature valid but the body is not something we can read.</summary>
    Malformed,

    /// <summary>Signature valid; an event type we do not act on.</summary>
    Ignored,
}

public sealed record WebhookParseResult(WebhookParseStatus Status, NormalisedPaymentEvent? Event = null)
{
    public static WebhookParseResult Invalid { get; } = new(WebhookParseStatus.InvalidSignature);

    public static WebhookParseResult Unreadable { get; } = new(WebhookParseStatus.Malformed);

    public static WebhookParseResult NotRelevant { get; } = new(WebhookParseStatus.Ignored);

    public static WebhookParseResult Of(NormalisedPaymentEvent evt) => new(WebhookParseStatus.Parsed, evt);
}

/// <summary>Everything a provider needs to open a hosted payment page. The amounts come from the order snapshot, never from the caller.</summary>
public sealed record CheckoutOrder(
    Guid OrderId, string PackName, int Credits, long SubtotalMinor, long TaxMinor, long TotalMinor, string Currency, decimal TaxPercent, string TaxLabel,
    string? CustomerName, string? CustomerEmail, DateTime ExpiresAt);

public sealed record CheckoutSession(string SessionId, string CheckoutUrl, DateTime ExpiresAt);

public sealed record ProviderRefund(string RefundId);

public enum ProviderPaymentStatus
{
    Pending = 0,
    Paid,
    Failed,
    Expired,
}

/// <summary>What the provider says about a checkout session when asked directly (server-to-server).</summary>
public sealed record ProviderPaymentState(ProviderPaymentStatus Status, string? PaymentId, long? AmountMinor, string? Currency);

/// <summary>A payment provider call failed (network, timeout, rejection). The message is safe to log; it never contains keys or card data.</summary>
public sealed class PaymentProviderException : Exception
{
    public PaymentProviderException(string message)
        : base(message)
    {
    }

    public PaymentProviderException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// A hosted-checkout payment provider. No card data ever reaches NexaVerify: the customer pays on the provider's page and the provider
/// tells us the result by a signed webhook (and, as a fallback, when we ask).
/// </summary>
public interface IPaymentProvider
{
    /// <summary>Lower-case name used in the webhook route and stored on orders.</summary>
    string Name { get; }

    Task<CheckoutSession> CreateCheckoutAsync(CheckoutOrder order, Uri successUrl, Uri cancelUrl, CancellationToken cancellationToken);

    /// <summary>
    /// Verifies the signature over the RAW body (constant-time) BEFORE reading anything from it, then parses it. Header names are matched
    /// case-insensitively. Must not throw for bad input: it answers <see cref="WebhookParseStatus.InvalidSignature"/> or
    /// <see cref="WebhookParseStatus.Malformed"/>.
    /// </summary>
    WebhookParseResult VerifyAndParseWebhook(IReadOnlyDictionary<string, string> headers, byte[] rawBody);

    /// <summary>Refunds (part of) a captured payment. <paramref name="idempotencyKey"/> makes a retry safe.</summary>
    Task<ProviderRefund> RefundAsync(string providerPaymentId, long amountMinor, string currency, string reason, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Asks the provider what became of a checkout session (server-side confirmation).</summary>
    Task<ProviderPaymentState> FetchPaymentAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Closes an unpaid hosted page so it can no longer be paid. Best effort.</summary>
    Task CancelCheckoutAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>Finds the provider named in a webhook route. Only the provider selected by <c>Billing:Provider</c> is ever returned.</summary>
public interface IPaymentProviderResolver
{
    /// <summary>The configured provider, or null when billing is off.</summary>
    IPaymentProvider? Current { get; }

    IPaymentProvider? Find(string name);
}

/// <summary>The header names a webhook request may contribute to signature checking (nothing else is read from the request).</summary>
public static class WebhookHeaders
{
    public static IReadOnlyList<string> Names { get; } = ["Stripe-Signature", "X-Razorpay-Signature", "X-Razorpay-Event-Id"];
}

// ---- Persistence -------------------------------------------------------------------------------------------------------------

public interface ICreditPackRepository
{
    /// <summary>Packs in display order. Not tracked.</summary>
    Task<IReadOnlyList<CreditPack>> ListAsync(bool activeOnly, bool publicOnly, CancellationToken cancellationToken);

    /// <summary>Tracked.</summary>
    Task<CreditPack?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> HasOrdersAsync(Guid packId, CancellationToken cancellationToken);

    void Add(CreditPack pack);

    void Remove(CreditPack pack);
}

public sealed record OrderRow(PaymentOrder Order, string ClientName);

public interface IPaymentOrderRepository
{
    /// <summary>Tracked.</summary>
    Task<PaymentOrder?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<PaymentOrder?> GetNoTrackingAsync(Guid id, CancellationToken cancellationToken);

    Task<OrderRow?> GetRowAsync(Guid id, CancellationToken cancellationToken);

    Task<PaymentOrder?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Pending orders whose hosted page is still open (the caller's tenant).</summary>
    Task<int> CountOpenAsync(DateTime now, CancellationToken cancellationToken);

    Task<(IReadOnlyList<OrderRow> Items, int Total)> ListAsync(
        Guid? clientId, PaymentOrderStatus? status, DateTime? from, DateTime? to, int skip, int take, CancellationToken cancellationToken);

    /// <summary>Platform scope: unpaid orders created before <paramref name="createdBefore"/>, oldest first.</summary>
    Task<IReadOnlyList<(Guid Id, Guid ClientId)>> ListPendingCreatedBeforeAsync(DateTime createdBefore, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<Refund>> GetRefundsAsync(Guid orderId, CancellationToken cancellationToken);

    Task<bool> RefundExistsAsync(string providerRefundId, CancellationToken cancellationToken);

    /// <summary>Tracked.</summary>
    Task<Refund?> GetRefundAsync(Guid refundId, CancellationToken cancellationToken);

    void Add(PaymentOrder order);

    void Add(Refund refund);
}

public interface IPaymentEventRepository
{
    void Add(PaymentEvent paymentEvent);
}

public interface IBillingProfileRepository
{
    /// <summary>The caller's tenant profile (tracked), if it was ever saved.</summary>
    Task<BillingProfile?> GetAsync(CancellationToken cancellationToken);

    void Add(BillingProfile profile);
}

/// <summary>
/// The single-statement transitions that races could corrupt. Each is one conditional UPDATE (exactly one concurrent caller wins) and runs
/// inside the caller's transaction.
/// </summary>
public interface IPaymentOrderAtomics
{
    /// <summary>Pending/Expired/Failed to Paid, only when amount and currency equal the order's. True for exactly one caller.</summary>
    Task<bool> TryMarkPaidAsync(Guid orderId, string providerPaymentId, long amountMinor, string currency, DateTime now, CancellationToken cancellationToken);

    /// <summary>Moves the order to <paramref name="to"/> if it is currently in one of <paramref name="from"/>.</summary>
    Task<bool> TryTransitionAsync(Guid orderId, IReadOnlyCollection<PaymentOrderStatus> from, PaymentOrderStatus to, string? reason, DateTime now, CancellationToken cancellationToken);

    /// <summary>Records the license and invoice number of a paid order.</summary>
    Task SetFulfilmentAsync(Guid orderId, Guid licenseId, string invoiceNumber, DateTime now, CancellationToken cancellationToken);

    /// <summary>Reserves <paramref name="amountMinor"/> of the order's refundable remainder (Paid/PartiallyRefunded only). False when it no longer fits.</summary>
    Task<bool> TryReserveRefundAsync(Guid orderId, long amountMinor, DateTime now, CancellationToken cancellationToken);

    /// <summary>Gives a reservation back (the provider refused the refund).</summary>
    Task ReleaseRefundAsync(Guid orderId, long amountMinor, DateTime now, CancellationToken cancellationToken);

    /// <summary>Completes a refund: books the credits revoked and sets Refunded / PartiallyRefunded.</summary>
    Task CompleteRefundAsync(Guid orderId, int creditsRevoked, DateTime now, CancellationToken cancellationToken);

    /// <summary>Claims the right to ask the provider about the order now (at most once per <paramref name="minInterval"/>). True for one caller.</summary>
    Task<bool> TryClaimCheckAsync(Guid orderId, DateTime now, TimeSpan minInterval, CancellationToken cancellationToken);
}

/// <summary>Allocates <c>INV-YYYY-000001</c> numbers. Must run inside the transaction that marks the order paid: a rollback gives the number back.</summary>
public interface IInvoiceNumberAllocator
{
    Task<string> NextAsync(int year, CancellationToken cancellationToken);
}

/// <summary>Shared (all API nodes) checkout limits per user and per client.</summary>
public interface IBillingThrottle
{
    /// <summary>Takes one permit from both budgets; false when either is used up.</summary>
    Task<bool> TryAcquireCheckoutAsync(Guid userId, Guid clientId, CancellationToken cancellationToken);
}
