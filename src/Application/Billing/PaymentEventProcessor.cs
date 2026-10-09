using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Api;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Licensing;
using NexaVerify.Application.Persistence;
using NexaVerify.Application.Public;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Domain.Api;
using NexaVerify.Domain.Billing;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.Billing;

/// <summary>What happened to a provider notification (stored on the event ledger row; no personal data).</summary>
public static class PaymentOutcomes
{
    public const string Granted = "Granted";
    public const string Duplicate = "Duplicate";
    public const string AlreadyPaid = "AlreadyPaid";
    public const string MarkedFailed = "MarkedFailed";
    public const string MarkedExpired = "MarkedExpired";
    public const string Ignored = "Ignored";
    public const string RefundKnown = "RefundKnown";

    // Anomalies: a verified provider event that does not fit our records. Nothing is granted; an operator must look.
    public const string OrderNotFound = "AnomalyOrderNotFound";
    public const string AmountMismatch = "AnomalyAmountMismatch";
    public const string ProviderMismatch = "AnomalyProviderMismatch";
    public const string NotPayable = "AnomalyNotPayable";
    public const string DuplicatePayment = "AnomalyDuplicatePayment";
    public const string ExternalRefund = "AnomalyExternalRefund";
    public const string MissingData = "AnomalyMissingData";

    public static bool IsAnomaly(string outcome) => outcome.StartsWith("Anomaly", StringComparison.Ordinal);
}

public interface IPaymentEventProcessor
{
    /// <summary>
    /// Applies a provider event that has ALREADY been authenticated (verified signature, or a server-to-server fetch). Idempotent: the event
    /// ledger's unique (provider, event id) makes a redelivery a no-op, and the order's conditional UPDATE makes a second event for the
    /// same payment (another delivery, a reconcile) grant nothing.
    /// </summary>
    Task<Result<string>> ProcessAsync(NormalisedPaymentEvent paymentEvent, CancellationToken cancellationToken);
}

public interface IPaymentReconciler
{
    /// <summary>Asks the provider what became of a payable order and applies the answer. Returns the outcome, or null if there was nothing to ask.</summary>
    Task<Result<string?>> ReconcileAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>For the maintenance job: reconciles, and expires the order if it is still unpaid after <c>Billing:PendingExpiryHours</c>.</summary>
    Task<Result<string?>> ReconcileOrExpireAsync(Guid orderId, CancellationToken cancellationToken);
}

/// <summary>
/// The one place a payment turns into credits. Everything that must be all-or-nothing happens in one transaction: the event ledger row,
/// the order's Paid transition (a single conditional UPDATE that only one concurrent caller wins), the invoice number, the top-up license
/// with its ledger Grant, the audit entry, the in-app notification and the outbound webhook event. The receipt email goes out after commit.
/// </summary>
public sealed partial class PaymentEventProcessor : IPaymentEventProcessor
{
    private readonly IPaymentOrderRepository _orders;
    private readonly IPaymentEventRepository _events;
    private readonly IPaymentOrderAtomics _atomics;
    private readonly IInvoiceNumberAllocator _invoices;
    private readonly ILicenseGrantService _grants;
    private readonly IWebhookPublisher _webhooks;
    private readonly ILicenseAlertRepository _alerts;
    private readonly IClientRepository _clients;
    private readonly IAuditService _audit;
    private readonly IEmailOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantScope _scope;
    private readonly ITenantContext _tenantContext;
    private readonly BillingOptions _options;
    private readonly PortalLinksOptions _portal;
    private readonly TimeProvider _time;
    private readonly ILogger<PaymentEventProcessor> _logger;

    public PaymentEventProcessor(
        IPaymentOrderRepository orders,
        IPaymentEventRepository events,
        IPaymentOrderAtomics atomics,
        IInvoiceNumberAllocator invoices,
        ILicenseGrantService grants,
        IWebhookPublisher webhooks,
        ILicenseAlertRepository alerts,
        IClientRepository clients,
        IAuditService audit,
        IEmailOutbox outbox,
        IUnitOfWork unitOfWork,
        ITenantScope scope,
        ITenantContext tenantContext,
        IOptions<BillingOptions> options,
        IOptions<PortalLinksOptions> portal,
        TimeProvider time,
        ILogger<PaymentEventProcessor> logger)
    {
        _orders = orders;
        _events = events;
        _atomics = atomics;
        _invoices = invoices;
        _grants = grants;
        _webhooks = webhooks;
        _alerts = alerts;
        _clients = clients;
        _audit = audit;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _scope = scope;
        _tenantContext = tenantContext;
        _options = options.Value;
        _portal = portal.Value;
        _time = time;
        _logger = logger;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<Result<string>> ProcessAsync(NormalisedPaymentEvent paymentEvent, CancellationToken cancellationToken)
    {
        PaymentOrder? order = null;
        if (paymentEvent.OrderId is { } orderId)
        {
            // The order's tenant is only known after looking it up, so the lookup is the one thing done in platform scope.
            // A signed-in client (the simulator, a status poll) may only ever see its own orders, and is not allowed into platform scope.
            using var lookup = _tenantContext.ClientId is null ? _scope.BeginPlatform("billing: locate the order of a payment event") : null;
            order = await _orders.GetNoTrackingAsync(orderId, cancellationToken);
        }

        // Everything else runs inside the owning tenant: the license, ledger and webhook queries are tenant-filtered, so a
        // platform-wide scope here would fan a webhook event out to every client.
        using var scope = order is not null
            ? _scope.BeginTenant(order.ClientId)
            : _tenantContext.ClientId is { } own ? _scope.BeginTenant(own) : _scope.BeginPlatform("billing: payment event without an order");

        Receipt? receipt = null;
        try
        {
            var outcome = await _unitOfWork.ExecuteInTransactionAsync(
                async ct =>
                {
                    var record = PaymentEvent.Receive(paymentEvent.Provider, paymentEvent.EventId, paymentEvent.Type.ToString(), paymentEvent.OrderId, Now);
                    _events.Add(record);

                    // The unique (provider, event id) index serialises redeliveries: a concurrent copy waits here for this transaction and
                    // then fails with a duplicate (or proceeds, if this one rolled back).
                    await _unitOfWork.SaveChangesAsync(ct);

                    var (code, made) = await ApplyAsync(paymentEvent, ct);
                    receipt = made;
                    record.Complete(code, Now);
                    await _unitOfWork.SaveChangesAsync(ct);
                    return code;
                },
                cancellationToken);

            if (receipt is not null)
            {
                await SendReceiptAsync(receipt, cancellationToken);
            }

            return outcome;
        }
        catch (UniqueConstraintViolationException)
        {
            _unitOfWork.ClearTracked();
            LogDuplicate(paymentEvent.Provider);
            return PaymentOutcomes.Duplicate;
        }
    }

    private async Task<(string Outcome, Receipt? Receipt)> ApplyAsync(NormalisedPaymentEvent evt, CancellationToken ct)
    {
        // Re-read inside the transaction: the decision below must see what earlier, concurrent events committed.
        var order = evt.OrderId is { } id ? await _orders.GetNoTrackingAsync(id, ct) : null;

        switch (evt.Type)
        {
            case PaymentEventType.PaymentSucceeded:
                return await ApplyPaymentSucceededAsync(evt, order, ct);

            case PaymentEventType.PaymentFailed:
                return (await ApplyTransitionAsync(order, [PaymentOrderStatus.Pending], PaymentOrderStatus.Failed, "billing.payment_failed", PaymentOutcomes.MarkedFailed, ct), null);

            case PaymentEventType.CheckoutExpired:
                return (await ApplyTransitionAsync(order, [PaymentOrderStatus.Pending], PaymentOrderStatus.Expired, "billing.checkout_expired", PaymentOutcomes.MarkedExpired, ct), null);

            case PaymentEventType.RefundCreated:
                if (evt.ProviderRefundId is { } refundId && await _orders.RefundExistsAsync(refundId, ct))
                {
                    return (PaymentOutcomes.RefundKnown, null);
                }

                LogExternalRefund(evt.Provider, evt.OrderId);
                return (PaymentOutcomes.ExternalRefund, null);

            default:
                return (PaymentOutcomes.Ignored, null);
        }
    }

    private async Task<(string Outcome, Receipt? Receipt)> ApplyPaymentSucceededAsync(NormalisedPaymentEvent evt, PaymentOrder? order, CancellationToken ct)
    {
        if (order is null)
        {
            LogOrderNotFound(evt.Provider, evt.OrderId);
            return (PaymentOutcomes.OrderNotFound, null);
        }

        if (!string.Equals(order.Provider, evt.Provider, StringComparison.OrdinalIgnoreCase))
        {
            LogAnomaly(PaymentOutcomes.ProviderMismatch, order.Id, order.ClientId);
            return (PaymentOutcomes.ProviderMismatch, null);
        }

        if (string.IsNullOrWhiteSpace(evt.ProviderPaymentId) || evt.AmountMinor is null || string.IsNullOrWhiteSpace(evt.Currency))
        {
            LogAnomaly(PaymentOutcomes.MissingData, order.Id, order.ClientId);
            return (PaymentOutcomes.MissingData, null);
        }

        // The amount the customer paid must be exactly what we asked for. Only the stored snapshot counts; the redirect, the request that
        // started the checkout and anything the browser says are never consulted.
        if (evt.AmountMinor != order.TotalMinor || !string.Equals(evt.Currency, order.Currency, StringComparison.OrdinalIgnoreCase))
        {
            LogAnomaly(PaymentOutcomes.AmountMismatch, order.Id, order.ClientId);
            _audit.Record(new AuditEntry("billing.payment_anomaly", nameof(PaymentOrder), order.Id.ToString(), order.ClientId,
                NewValues: new { Outcome = PaymentOutcomes.AmountMismatch, Expected = order.TotalMinor, order.Currency, Received = evt.AmountMinor, ReceivedCurrency = evt.Currency }));
            return (PaymentOutcomes.AmountMismatch, null);
        }

        if (order.Status is PaymentOrderStatus.Paid or PaymentOrderStatus.Refunded or PaymentOrderStatus.PartiallyRefunded)
        {
            if (string.Equals(order.ProviderPaymentId, evt.ProviderPaymentId, StringComparison.Ordinal))
            {
                return (PaymentOutcomes.AlreadyPaid, null);
            }

            LogAnomaly(PaymentOutcomes.DuplicatePayment, order.Id, order.ClientId);
            _audit.Record(new AuditEntry("billing.payment_anomaly", nameof(PaymentOrder), order.Id.ToString(), order.ClientId,
                NewValues: new { Outcome = PaymentOutcomes.DuplicatePayment }));
            return (PaymentOutcomes.DuplicatePayment, null);
        }

        if (!order.IsPayable)
        {
            LogAnomaly(PaymentOutcomes.NotPayable, order.Id, order.ClientId);
            _audit.Record(new AuditEntry("billing.payment_anomaly", nameof(PaymentOrder), order.Id.ToString(), order.ClientId,
                NewValues: new { Outcome = PaymentOutcomes.NotPayable, Status = order.Status.ToString() }));
            return (PaymentOutcomes.NotPayable, null);
        }

        var now = Now;
        if (!await _atomics.TryMarkPaidAsync(order.Id, evt.ProviderPaymentId, order.TotalMinor, order.Currency, now, ct))
        {
            return (PaymentOutcomes.AlreadyPaid, null); // another event won the conditional UPDATE
        }

        var expiresAt = now.AddDays(order.ValidityDays);
        var license = await _grants.CreateGrantAsync(
            order.ClientId,
            new CreateLicenseRequest(null, Truncate("Top-up: " + order.PackName, 150), order.Credits, now, expiresAt, $"Online purchase, order {order.Id:D}"),
            $"Top-up purchase, order {order.Id:D}",
            "billing",
            ct);
        if (license.IsFailure)
        {
            // Roll everything back (the order stays payable) and let the provider retry / the reconcile job pick it up.
            throw new BillingFulfilmentException(license.Error!.Code);
        }

        var invoiceNumber = await _invoices.NextAsync(now.Year, ct);
        await _atomics.SetFulfilmentAsync(order.Id, license.Value.Id, invoiceNumber, now, ct);

        _audit.Record(new AuditEntry("billing.payment_succeeded", nameof(PaymentOrder), order.Id.ToString(), order.ClientId,
            NewValues: new { OrderId = order.Id, LicenseId = license.Value.Id, InvoiceNumber = invoiceNumber, order.Credits, order.TotalMinor, order.Currency, order.Provider }));
        await _webhooks.PublishAsync(order.ClientId, WebhookEvents.LicenseToppedUp, new
        {
            orderId = order.Id,
            licenseId = license.Value.Id,
            credits = order.Credits,
            expiresAt,
            invoiceNumber,
            totalMinor = order.TotalMinor,
            currency = order.Currency,
        }, ct);
        _alerts.Add(LicenseAlert.Create(
            order.ClientId, LicenseAlertType.PaymentReceived, order.Id, "paid", AlertSeverity.Info, "Credits added",
            $"Your purchase \"{order.PackName}\" was paid: {order.Credits} credits were added to your account. Invoice {invoiceNumber}.", now));

        LogGranted(order.Id, order.ClientId, license.Value.Id, order.Credits);
        return (PaymentOutcomes.Granted, new Receipt(order.Id, order.ClientId, order.PackName, order.Credits, order.TotalMinor, order.Currency, invoiceNumber, expiresAt, order.BuyerJson));
    }

    private async Task<string> ApplyTransitionAsync(
        PaymentOrder? order, IReadOnlyCollection<PaymentOrderStatus> from, PaymentOrderStatus to, string auditAction, string outcome, CancellationToken ct)
    {
        if (order is null)
        {
            LogOrderNotFound("event", null);
            return PaymentOutcomes.OrderNotFound;
        }

        if (!await _atomics.TryTransitionAsync(order.Id, from, to, null, Now, ct))
        {
            return PaymentOutcomes.Ignored; // not in a state this event can change (already paid, cancelled, ...)
        }

        _audit.Record(new AuditEntry(auditAction, nameof(PaymentOrder), order.Id.ToString(), order.ClientId, NewValues: new { Status = to.ToString() }));
        LogTransition(order.Id, to.ToString());
        return outcome;
    }

    private async Task SendReceiptAsync(Receipt receipt, CancellationToken cancellationToken)
    {
        var buyer = BuyerSnapshot.Parse(receipt.BuyerJson);
        var to = buyer.BillingEmail;
        if (string.IsNullOrWhiteSpace(to))
        {
            to = (await _clients.GetByIdAsync(receipt.ClientId, cancellationToken))?.ContactEmail ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(to))
        {
            return;
        }

        var link = _portal.LinkTo($"billing/orders/{receipt.OrderId:D}");
        _outbox.Enqueue(new EmailMessage(
            to,
            $"Payment received - invoice {receipt.InvoiceNumber}",
            $"Hello,\n\nWe received your payment of {Money.Format(receipt.TotalMinor, receipt.Currency)} for \"{Plain(receipt.PackName)}\".\n"
            + $"{receipt.Credits.ToString("N0", CultureInfo.InvariantCulture)} credits were added to your account and stay valid until {receipt.ExpiresAt.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)} (UTC).\n\n"
            + $"Invoice: {receipt.InvoiceNumber}\nView the order and print the invoice: {link}\n\nThank you."));
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string Plain(string value) => value.ReplaceLineEndings(" ").Trim();

    private sealed record Receipt(
        Guid OrderId, Guid ClientId, string PackName, int Credits, long TotalMinor, string Currency, string InvoiceNumber, DateTime ExpiresAt, string BuyerJson);

    [LoggerMessage(EventId = 9003, Level = LogLevel.Information, Message = "Payment confirmed: order {OrderId} of client {ClientId} granted {Credits} credits on license {LicenseId}")]
    private partial void LogGranted(Guid orderId, Guid clientId, Guid licenseId, int credits);

    [LoggerMessage(EventId = 9005, Level = LogLevel.Information, Message = "Payment event from {Provider} ignored: already processed")]
    private partial void LogDuplicate(string provider);

    [LoggerMessage(EventId = 9006, Level = LogLevel.Information, Message = "Order {OrderId} is now {Status}")]
    private partial void LogTransition(Guid orderId, string status);

    [LoggerMessage(EventId = 9010, Level = LogLevel.Error, Message = "BILLING ANOMALY {Anomaly}: a verified payment for order {OrderId} (client {ClientId}) was NOT granted; review it with the payment provider")]
    private partial void LogAnomaly(string anomaly, Guid orderId, Guid clientId);

    [LoggerMessage(EventId = 9011, Level = LogLevel.Warning, Message = "BILLING ANOMALY: {Provider} reports a refund (order {OrderId}) that was not made through NexaVerify; credits were not changed, reconcile it manually")]
    private partial void LogExternalRefund(string provider, Guid? orderId);

    [LoggerMessage(EventId = 9013, Level = LogLevel.Warning, Message = "BILLING ANOMALY: {Provider} payment event refers to an unknown order {OrderId}")]
    private partial void LogOrderNotFound(string provider, Guid? orderId);
}

/// <summary>The top-up license could not be created for a confirmed payment. The transaction rolls back and the provider (or the job) retries.</summary>
public sealed class BillingFulfilmentException : Exception
{
    public BillingFulfilmentException(string code)
        : base("Could not fulfil a confirmed payment (" + code + ").")
    {
    }
}

/// <summary>Server-side confirmation: when the webhook is late (or lost) the platform asks the provider itself.</summary>
public sealed partial class PaymentReconciler : IPaymentReconciler
{
    private readonly IPaymentOrderRepository _orders;
    private readonly IPaymentOrderAtomics _atomics;
    private readonly IPaymentProviderResolver _providers;
    private readonly IPaymentEventProcessor _processor;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantScope _scope;
    private readonly BillingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PaymentReconciler> _logger;

    public PaymentReconciler(
        IPaymentOrderRepository orders,
        IPaymentOrderAtomics atomics,
        IPaymentProviderResolver providers,
        IPaymentEventProcessor processor,
        IAuditService audit,
        IUnitOfWork unitOfWork,
        ITenantScope scope,
        IOptions<BillingOptions> options,
        TimeProvider time,
        ILogger<PaymentReconciler> logger)
    {
        _orders = orders;
        _atomics = atomics;
        _providers = providers;
        _processor = processor;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _scope = scope;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<Result<string?>> ReconcileAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await _orders.GetNoTrackingAsync(orderId, cancellationToken);
        if (order is null)
        {
            return Error.NotFound();
        }

        if (!order.IsPayable || order.ProviderSessionId is null)
        {
            return Result<string?>.Success(null);
        }

        var provider = _providers.Find(order.Provider);
        if (provider is null)
        {
            return Error.Unavailable(Contracts.Common.ErrorCodes.PaymentProviderUnavailable, "The payment provider of this order is not configured.");
        }

        ProviderPaymentState state;
        try
        {
            state = await provider.FetchPaymentAsync(order.ProviderSessionId, cancellationToken);
        }
        catch (PaymentProviderException ex)
        {
            LogFetchFailed(order.Id, ex.Message);
            return Error.Unavailable(Contracts.Common.ErrorCodes.PaymentProviderUnavailable, "The payment provider could not be reached.");
        }

        var evt = state.Status switch
        {
            ProviderPaymentStatus.Paid => new NormalisedPaymentEvent(
                provider.Name, $"fetch:{order.ProviderSessionId}:{state.PaymentId}", PaymentEventType.PaymentSucceeded, order.Id, state.PaymentId, state.AmountMinor, state.Currency),
            ProviderPaymentStatus.Failed => new NormalisedPaymentEvent(
                provider.Name, $"fetch:{order.ProviderSessionId}:failed", PaymentEventType.PaymentFailed, order.Id, null, null, null),
            ProviderPaymentStatus.Expired => new NormalisedPaymentEvent(
                provider.Name, $"fetch:{order.ProviderSessionId}:expired", PaymentEventType.CheckoutExpired, order.Id, null, null, null),
            _ => null,
        };
        if (evt is null)
        {
            return Result<string?>.Success(null);
        }

        var outcome = await _processor.ProcessAsync(evt, cancellationToken);
        return outcome.IsSuccess ? Result<string?>.Success(outcome.Value) : outcome.Error!;
    }

    public async Task<Result<string?>> ReconcileOrExpireAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var reconciled = await ReconcileAsync(orderId, cancellationToken);
        var order = await _orders.GetNoTrackingAsync(orderId, cancellationToken);
        var cutoff = _time.GetUtcNow().UtcDateTime.AddHours(-_options.PendingExpiryHours);
        if (order is { Status: PaymentOrderStatus.Pending } && order.CreatedAt <= cutoff)
        {
            // Close the hosted page first (best effort) so it can not be paid after we gave up on it.
            if (order.ProviderSessionId is not null && _providers.Find(order.Provider) is { } provider)
            {
                try
                {
                    await provider.CancelCheckoutAsync(order.ProviderSessionId, cancellationToken);
                }
                catch (PaymentProviderException ex)
                {
                    LogFetchFailed(order.Id, ex.Message);
                }
            }

            using var tenant = _scope.BeginTenant(order.ClientId);
            var expired = await _unitOfWork.ExecuteInTransactionAsync(
                async ct =>
                {
                    if (!await _atomics.TryTransitionAsync(order.Id, [PaymentOrderStatus.Pending], PaymentOrderStatus.Expired, "Never paid", _time.GetUtcNow().UtcDateTime, ct))
                    {
                        return false;
                    }

                    _audit.Record(new AuditEntry("billing.order_expired", nameof(PaymentOrder), order.Id.ToString(), order.ClientId, NewValues: new { Status = "Expired" }));
                    await _unitOfWork.SaveChangesAsync(ct);
                    return true;
                },
                cancellationToken);
            if (expired)
            {
                return PaymentOutcomes.MarkedExpired;
            }
        }

        return reconciled;
    }

    [LoggerMessage(EventId = 9016, Level = LogLevel.Warning, Message = "Could not ask the payment provider about order {OrderId}: {Reason}")]
    private partial void LogFetchFailed(Guid orderId, string reason);
}
