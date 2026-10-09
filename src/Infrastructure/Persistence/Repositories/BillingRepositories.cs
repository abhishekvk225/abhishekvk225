using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Billing;
using NexaVerify.Domain.Billing;

namespace NexaVerify.Infrastructure.Persistence.Repositories;

internal sealed class CreditPackRepository : ICreditPackRepository
{
    private readonly AppDbContext _db;

    public CreditPackRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<CreditPack>> ListAsync(bool activeOnly, bool publicOnly, CancellationToken cancellationToken)
    {
        var query = _db.CreditPacks.AsNoTracking().AsQueryable();
        if (activeOnly)
        {
            query = query.Where(p => p.IsActive);
        }

        if (publicOnly)
        {
            query = query.Where(p => p.IsPublic);
        }

        return await query.OrderBy(p => p.DisplayOrder).ThenBy(p => p.PriceMinor).ThenBy(p => p.Name).ToListAsync(cancellationToken);
    }

    public Task<CreditPack?> GetAsync(Guid id, CancellationToken cancellationToken) => _db.CreditPacks.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<bool> HasOrdersAsync(Guid packId, CancellationToken cancellationToken) => _db.PaymentOrders.AnyAsync(o => o.PackId == packId, cancellationToken);

    public void Add(CreditPack pack) => _db.CreditPacks.Add(pack);

    public void Remove(CreditPack pack) => _db.CreditPacks.Remove(pack);
}

internal sealed class PaymentOrderRepository : IPaymentOrderRepository
{
    private readonly AppDbContext _db;

    public PaymentOrderRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<PaymentOrder?> GetAsync(Guid id, CancellationToken cancellationToken) => _db.PaymentOrders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public Task<PaymentOrder?> GetNoTrackingAsync(Guid id, CancellationToken cancellationToken) =>
        _db.PaymentOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task<OrderRow?> GetRowAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await (from o in _db.PaymentOrders.AsNoTracking()
                         where o.Id == id
                         join c in _db.Clients.AsNoTracking() on o.ClientId equals c.Id
                         select new { Order = o, ClientName = c.Name }).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new OrderRow(row.Order, row.ClientName);
    }

    public Task<PaymentOrder?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        _db.PaymentOrders.AsNoTracking().FirstOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey, cancellationToken);

    public Task<int> CountOpenAsync(DateTime now, CancellationToken cancellationToken) =>
        _db.PaymentOrders.CountAsync(o => o.Status == PaymentOrderStatus.Pending && o.ExpiresAt > now, cancellationToken);

    public async Task<(IReadOnlyList<OrderRow> Items, int Total)> ListAsync(
        Guid? clientId, PaymentOrderStatus? status, DateTime? from, DateTime? to, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _db.PaymentOrders.AsNoTracking().AsQueryable();
        if (clientId is { } cid)
        {
            query = query.Where(o => o.ClientId == cid);
        }

        if (status is { } s)
        {
            query = query.Where(o => o.Status == s);
        }

        if (from is { } f)
        {
            query = query.Where(o => o.CreatedAt >= f);
        }

        if (to is { } t)
        {
            query = query.Where(o => o.CreatedAt < t);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await (from o in query
                          join c in _db.Clients.AsNoTracking() on o.ClientId equals c.Id
                          select new { Order = o, ClientName = c.Name })
            .OrderByDescending(x => x.Order.CreatedAt).ThenBy(x => x.Order.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (rows.Select(r => new OrderRow(r.Order, r.ClientName)).ToList(), total);
    }

    public async Task<IReadOnlyList<(Guid Id, Guid ClientId)>> ListPendingCreatedBeforeAsync(DateTime createdBefore, int take, CancellationToken cancellationToken)
    {
        var rows = await _db.PaymentOrders.AsNoTracking()
            .Where(o => o.Status == PaymentOrderStatus.Pending && o.CreatedAt <= createdBefore)
            .OrderBy(o => o.CreatedAt)
            .Select(o => new { o.Id, o.ClientId })
            .Take(take)
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.Id, r.ClientId)).ToList();
    }

    public async Task<IReadOnlyList<Refund>> GetRefundsAsync(Guid orderId, CancellationToken cancellationToken) =>
        await _db.Refunds.AsNoTracking().Where(r => r.OrderId == orderId).OrderBy(r => r.CreatedAt).ToListAsync(cancellationToken);

    public Task<bool> RefundExistsAsync(string providerRefundId, CancellationToken cancellationToken) =>
        _db.Refunds.AnyAsync(r => r.ProviderRefundId == providerRefundId, cancellationToken);

    public Task<Refund?> GetRefundAsync(Guid refundId, CancellationToken cancellationToken) => _db.Refunds.FirstOrDefaultAsync(r => r.Id == refundId, cancellationToken);

    public void Add(PaymentOrder order) => _db.PaymentOrders.Add(order);

    public void Add(Refund refund) => _db.Refunds.Add(refund);
}

internal sealed class PaymentEventRepository : IPaymentEventRepository
{
    private readonly AppDbContext _db;

    public PaymentEventRepository(AppDbContext db)
    {
        _db = db;
    }

    public void Add(PaymentEvent paymentEvent) => _db.PaymentEvents.Add(paymentEvent);
}

internal sealed class BillingProfileRepository : IBillingProfileRepository
{
    private readonly AppDbContext _db;

    public BillingProfileRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<BillingProfile?> GetAsync(CancellationToken cancellationToken) => _db.BillingProfiles.FirstOrDefaultAsync(cancellationToken);

    public void Add(BillingProfile profile) => _db.BillingProfiles.Add(profile);
}
