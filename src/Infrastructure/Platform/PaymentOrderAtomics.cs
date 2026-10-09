using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Billing;
using NexaVerify.Domain.Billing;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Platform;

/// <summary>
/// The single-statement order transitions. Each is ONE conditional UPDATE whose WHERE clause carries the whole rule (current state,
/// amount, remaining refundable money), so under any number of concurrent webhook deliveries, reconcile calls and admin clicks exactly
/// one caller changes the row and every other sees "0 rows". They run inside the caller's transaction.
/// </summary>
public sealed class PaymentOrderAtomics : IPaymentOrderAtomics
{
    private readonly AppDbContext _db;

    public PaymentOrderAtomics(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> TryMarkPaidAsync(Guid orderId, string providerPaymentId, long amountMinor, string currency, DateTime now, CancellationToken cancellationToken)
    {
        var payable = PaymentOrder.PayableStatuses.ToArray();
        return await _db.PaymentOrders
            .Where(o => o.Id == orderId && payable.Contains(o.Status) && o.TotalMinor == amountMinor && o.Currency == currency)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, PaymentOrderStatus.Paid)
                .SetProperty(o => o.ProviderPaymentId, providerPaymentId)
                .SetProperty(o => o.PaidAt, (DateTime?)now)
                .SetProperty(o => o.UpdatedAt, (DateTime?)now), cancellationToken) == 1;
    }

    public async Task<bool> TryTransitionAsync(
        Guid orderId, IReadOnlyCollection<PaymentOrderStatus> from, PaymentOrderStatus to, string? reason, DateTime now, CancellationToken cancellationToken)
    {
        var allowed = from.ToArray();
        var text = reason is { Length: > 200 } ? reason[..200] : reason;
        return await _db.PaymentOrders
            .Where(o => o.Id == orderId && allowed.Contains(o.Status))
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, to)
                .SetProperty(o => o.FailureReason, text)
                .SetProperty(o => o.UpdatedAt, (DateTime?)now), cancellationToken) == 1;
    }

    public Task SetFulfilmentAsync(Guid orderId, Guid licenseId, string invoiceNumber, DateTime now, CancellationToken cancellationToken) =>
        _db.PaymentOrders
            .Where(o => o.Id == orderId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.LicenseId, (Guid?)licenseId)
                .SetProperty(o => o.InvoiceNumber, invoiceNumber)
                .SetProperty(o => o.UpdatedAt, (DateTime?)now), cancellationToken);

    public async Task<bool> TryReserveRefundAsync(Guid orderId, long amountMinor, DateTime now, CancellationToken cancellationToken)
    {
        var refundable = PaymentOrder.RefundableStatuses.ToArray();

        // Two mutually exclusive statements: this refund uses up the remainder (Refunded), or leaves some (PartiallyRefunded).
        var full = await _db.PaymentOrders
            .Where(o => o.Id == orderId && refundable.Contains(o.Status) && o.RefundedMinor + amountMinor == o.TotalMinor)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.RefundedMinor, o => o.RefundedMinor + amountMinor)
                .SetProperty(o => o.Status, PaymentOrderStatus.Refunded)
                .SetProperty(o => o.UpdatedAt, (DateTime?)now), cancellationToken);
        if (full == 1)
        {
            return true;
        }

        return await _db.PaymentOrders
            .Where(o => o.Id == orderId && refundable.Contains(o.Status) && o.RefundedMinor + amountMinor < o.TotalMinor)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.RefundedMinor, o => o.RefundedMinor + amountMinor)
                .SetProperty(o => o.Status, PaymentOrderStatus.PartiallyRefunded)
                .SetProperty(o => o.UpdatedAt, (DateTime?)now), cancellationToken) == 1;
    }

    public async Task ReleaseRefundAsync(Guid orderId, long amountMinor, DateTime now, CancellationToken cancellationToken)
    {
        var settled = await _db.PaymentOrders
            .Where(o => o.Id == orderId && o.RefundedMinor - amountMinor == 0 && o.RefundedMinor >= amountMinor)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.RefundedMinor, 0L)
                .SetProperty(o => o.Status, PaymentOrderStatus.Paid)
                .SetProperty(o => o.UpdatedAt, (DateTime?)now), cancellationToken);
        if (settled == 0)
        {
            await _db.PaymentOrders
                .Where(o => o.Id == orderId && o.RefundedMinor - amountMinor > 0)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.RefundedMinor, o => o.RefundedMinor - amountMinor)
                    .SetProperty(o => o.Status, PaymentOrderStatus.PartiallyRefunded)
                    .SetProperty(o => o.UpdatedAt, (DateTime?)now), cancellationToken);
        }
    }

    public Task CompleteRefundAsync(Guid orderId, int creditsRevoked, DateTime now, CancellationToken cancellationToken) =>
        _db.PaymentOrders
            .Where(o => o.Id == orderId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.CreditsRevoked, o => o.CreditsRevoked + creditsRevoked)
                .SetProperty(o => o.UpdatedAt, (DateTime?)now), cancellationToken);

    public async Task<bool> TryClaimCheckAsync(Guid orderId, DateTime now, TimeSpan minInterval, CancellationToken cancellationToken)
    {
        var earliest = now - minInterval;
        return await _db.PaymentOrders
            .Where(o => o.Id == orderId && (o.LastCheckedAt == null || o.LastCheckedAt <= earliest))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.LastCheckedAt, (DateTime?)now), cancellationToken) == 1;
    }
}
