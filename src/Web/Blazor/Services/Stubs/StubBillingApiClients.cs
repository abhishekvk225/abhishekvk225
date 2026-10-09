using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Dashboards;

namespace NexaVerify.Web.Services;

/// <summary>
/// In-memory billing data for the design-review stubs (<c>Ui:UseStubClients</c>, Development/UiDemo only). One singleton shared by the
/// client, admin and simulator stubs, so the buy -> pay -> return flow can be shown without an API or a payment provider.
/// </summary>
public sealed class StubBillingStore
{
    private readonly Lock _gate = new();
    private readonly List<AdminPackDto> _packs;
    private readonly List<AdminOrderDto> _orders;
    private int _invoiceSeq = 1042;
    private BillingProfileDto _profile = new("Acme Corp Ltd", "1 High Street", null, "London", null, "EC1A 1BB", "GB", "GB123456789", "billing@acme.example", "AAAAAAAB");

    public StubBillingStore(TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        _packs =
        [
            new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"), "Starter pack", "For one site or a small team.", 5000, 365, 4900, "USD",
                ["5,000 credits", "Valid for 12 months", "Email support"], 1, true, true, "AAAAAAAC"),
            new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"), "Growth pack", "For busy teams and API traffic.", 25000, 365, 19900, "USD",
                ["25,000 credits", "Valid for 12 months", "Priority support"], 2, true, true, "AAAAAAAD"),
            new(Guid.Parse("00000000-0000-0000-0000-0000000000b3"), "Scale pack", "For several sites and heavy traffic.", 100000, 365, 69900, "USD",
                ["100,000 credits", "Valid for 12 months", "Priority support", "Team roles and MFA"], 3, true, true, "AAAAAAAE"),
            new(Guid.Parse("00000000-0000-0000-0000-0000000000b4"), "Pilot pack (retired)", null, 1000, 90, 1500, "USD", [], 9, false, false, "AAAAAAAF"),
        ];
        var acme = Guid.Parse("00000000-0000-0000-0000-00000000c001");
        _orders =
        [
            Order(Guid.Parse("00000000-0000-0000-0000-00000000d001"), "NV-2026-001041", acme, _packs[0], OrderStatuses.Paid, now.AddDays(-20), now.AddDays(-20)),
            Order(Guid.Parse("00000000-0000-0000-0000-00000000d002"), null, acme, _packs[1], OrderStatuses.Failed, now.AddDays(-9), null),
            Order(Guid.Parse("00000000-0000-0000-0000-00000000d003"), "NV-2026-001040", acme, _packs[0], OrderStatuses.PartiallyRefunded, now.AddDays(-40), now.AddDays(-40)),
        ];
        _orders[2] = _orders[2] with { Refunds = [new RefundDto(Guid.NewGuid(), 1000, 1000, "Duplicate purchase", now.AddDays(-38))] };
    }

    public static Guid StubClientId { get; } = Guid.Parse("00000000-0000-0000-0000-00000000c001");

    public BillingProfileDto Profile { get { lock (_gate) { return _profile; } } }

    public BillingProfileDto SaveProfile(SaveBillingProfileRequest r)
    {
        lock (_gate)
        {
            return _profile = new BillingProfileDto(r.LegalName, r.AddressLine1, r.AddressLine2, r.City, r.State, r.PostalCode, r.Country, r.TaxId, r.BillingEmail,
                Guid.NewGuid().ToString("N")[..8]);
        }
    }

    public IReadOnlyList<AdminPackDto> Packs { get { lock (_gate) { return _packs.ToList(); } } }

    public AdminPackDto SavePack(Guid? id, SaveAdminPackRequest r)
    {
        lock (_gate)
        {
            var saved = new AdminPackDto(id ?? Guid.NewGuid(), r.Name, r.Description, r.Credits, r.ValidityDays, r.PriceMinor, r.Currency, r.Highlights, r.DisplayOrder,
                r.IsActive, r.IsPublic, Guid.NewGuid().ToString("N")[..8]);
            var index = id is null ? -1 : _packs.FindIndex(p => p.Id == id);
            if (index >= 0)
            {
                _packs[index] = saved;
            }
            else
            {
                _packs.Add(saved);
            }

            return saved;
        }
    }

    public IReadOnlyList<AdminOrderDto> Orders { get { lock (_gate) { return _orders.OrderByDescending(o => o.CreatedAt).ToList(); } } }

    public AdminOrderDto? Find(Guid id)
    {
        lock (_gate)
        {
            return _orders.FirstOrDefault(o => o.Id == id);
        }
    }

    public AdminOrderDto CreateOrder(Guid packId, TimeProvider clock)
    {
        lock (_gate)
        {
            var pack = _packs.First(p => p.Id == packId);
            var order = Order(Guid.NewGuid(), null, StubClientId, pack, OrderStatuses.Pending, clock.GetUtcNow(), null);
            _orders.Add(order);
            return order;
        }
    }

    /// <summary>What the payment partner would report: success, failed or cancelled.</summary>
    public AdminOrderDto? Simulate(Guid id, string outcome, TimeProvider clock)
    {
        lock (_gate)
        {
            var index = _orders.FindIndex(o => o.Id == id);
            if (index < 0)
            {
                return null;
            }

            var order = _orders[index];
            if (order.Status != OrderStatuses.Pending)
            {
                return order;
            }

            order = outcome switch
            {
                "success" => order with { Status = OrderStatuses.Paid, PaidAt = clock.GetUtcNow(), InvoiceNumber = $"NV-2026-00{++_invoiceSeq}", LicenseId = Guid.NewGuid() },
                "failed" => order with { Status = OrderStatuses.Failed },
                _ => order with { Status = OrderStatuses.Cancelled },
            };
            _orders[index] = order;
            return order;
        }
    }

    public AdminOrderDto? Refund(Guid id, RefundOrderRequest request, TimeProvider clock)
    {
        lock (_gate)
        {
            var index = _orders.FindIndex(o => o.Id == id);
            if (index < 0)
            {
                return null;
            }

            var order = _orders[index];
            var refunded = (order.Refunds ?? []).Sum(r => r.AmountMinor);
            var amount = request.AmountMinor ?? order.TotalMinor - refunded;
            var credits = (int)(order.Credits * amount / Math.Max(1, order.TotalMinor));
            var refunds = (order.Refunds ?? []).Append(new RefundDto(Guid.NewGuid(), amount, credits, request.Reason, clock.GetUtcNow())).ToList();
            order = order with
            {
                Refunds = refunds,
                Status = refunded + amount >= order.TotalMinor ? OrderStatuses.Refunded : OrderStatuses.PartiallyRefunded,
            };
            _orders[index] = order;
            return order;
        }
    }

    private static AdminOrderDto Order(Guid id, string? invoice, Guid clientId, AdminPackDto pack, string status, DateTimeOffset created, DateTimeOffset? paid)
    {
        var tax = pack.PriceMinor * 20 / 100;
        return new AdminOrderDto(id, invoice, clientId, "Acme Corp", pack.Name, pack.Credits, pack.ValidityDays, pack.PriceMinor, tax, pack.PriceMinor + tax, 20m, "VAT",
            pack.Currency, status, created, paid, paid is null ? null : Guid.NewGuid(), "demo-pay", paid is null ? null : "pay_" + id.ToString("N")[..10], [],
            [new OrderEventDto("order.created", created, "Order created"), .. paid is null ? Array.Empty<OrderEventDto>() : [new OrderEventDto("payment.succeeded", paid.Value, "Payment confirmed")]]);
    }
}

public sealed class StubBillingApiClient(StubBillingStore store, TimeProvider clock) : IBillingApiClient
{
    public Task<ApiResult<IReadOnlyList<CreditPackDto>>> GetPacksAsync(CancellationToken ct = default) =>
        Task.FromResult(ApiResult<IReadOnlyList<CreditPackDto>>.Ok(
            store.Packs.Where(p => p.IsActive).OrderBy(p => p.DisplayOrder)
                .Select(p => new CreditPackDto(p.Id, p.Name, p.Description, p.Credits, p.ValidityDays, p.PriceMinor, p.Currency, 20m, "VAT", p.PriceMinor + p.PriceMinor * 20 / 100, p.Highlights))
                .ToList()));

    public Task<ApiResult<CheckoutResponse>> CheckoutAsync(Guid packId, string idempotencyKey, CancellationToken ct = default)
    {
        var order = store.CreateOrder(packId, clock);
        return Task.FromResult(ApiResult<CheckoutResponse>.Ok(new CheckoutResponse(order.Id, CheckoutRedirect.DevPayPrefix + order.Id, clock.GetUtcNow().AddMinutes(30))));
    }

    public Task<ApiResult<PagedResult<OrderListItemDto>>> ListOrdersAsync(PageRequest page, string? status, CancellationToken ct = default)
    {
        var all = store.Orders.Where(o => o.ClientId == StubBillingStore.StubClientId && (string.IsNullOrEmpty(status) || o.Status == status)).ToList();
        var items = all.Skip((page.Page - 1) * page.PageSize).Take(page.PageSize)
            .Select(o => new OrderListItemDto(o.Id, o.InvoiceNumber, o.PackName, o.Credits, o.TotalMinor, o.Currency, o.Status, o.CreatedAt, o.PaidAt)).ToList();
        return Task.FromResult(ApiResult<PagedResult<OrderListItemDto>>.Ok(new PagedResult<OrderListItemDto>(items, page.Page, page.PageSize, all.Count)));
    }

    public Task<ApiResult<OrderDto>> GetOrderAsync(Guid id, CancellationToken ct = default, bool passive = false)
    {
        var o = store.Find(id);
        return Task.FromResult(o is null
            ? ApiResult<OrderDto>.Fail("NOT_FOUND", "We couldn't find that. It may have been removed.", "demo-order", 404)
            : ApiResult<OrderDto>.Ok(new OrderDto(o.Id, o.InvoiceNumber, o.PackName, o.Credits, o.ValidityDays, o.SubtotalMinor, o.TaxMinor, o.TotalMinor, o.TaxPercent, o.TaxLabel,
                o.Currency, o.Status, o.CreatedAt, o.PaidAt, o.LicenseId)));
    }

    public Task<ApiResult<BillingProfileDto>> GetProfileAsync(CancellationToken ct = default) => Task.FromResult(ApiResult<BillingProfileDto>.Ok(store.Profile));

    public Task<ApiResult<BillingProfileDto>> SaveProfileAsync(SaveBillingProfileRequest request, CancellationToken ct = default) =>
        Task.FromResult(request.LegalName.StartsWith("fail", StringComparison.OrdinalIgnoreCase)
            ? ApiResult<BillingProfileDto>.Fail(new ApiError("VALIDATION_FAILED", "Some of the details are not valid.", "demo-profile", 422,
                new Dictionary<string, string[]> { ["legalName"] = ["That company name was not accepted."] }))
            : ApiResult<BillingProfileDto>.Ok(store.SaveProfile(request)));
}

public sealed class StubAdminBillingApiClient(StubBillingStore store, TimeProvider clock) : IAdminBillingApiClient
{
    public Task<ApiResult<IReadOnlyList<AdminPackDto>>> ListPacksAsync(CancellationToken ct = default) =>
        Task.FromResult(ApiResult<IReadOnlyList<AdminPackDto>>.Ok(store.Packs.OrderBy(p => p.DisplayOrder).ToList()));

    public Task<ApiResult<AdminPackDto>> CreatePackAsync(SaveAdminPackRequest request, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<AdminPackDto>.Ok(store.SavePack(null, request)));

    public Task<ApiResult<AdminPackDto>> UpdatePackAsync(Guid id, SaveAdminPackRequest request, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<AdminPackDto>.Ok(store.SavePack(id, request)));

    public Task<ApiResult<PagedResult<AdminOrderListItemDto>>> ListOrdersAsync(AdminOrderQuery query, CancellationToken ct = default)
    {
        var all = store.Orders.Where(o => (query.ClientId is null || o.ClientId == query.ClientId) && (string.IsNullOrEmpty(query.Status) || o.Status == query.Status)).ToList();
        var items = all.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(o => new AdminOrderListItemDto(o.Id, o.InvoiceNumber, o.ClientId, o.ClientName, o.PackName, o.Credits, o.TotalMinor, o.Currency, o.Status, o.CreatedAt, o.PaidAt)).ToList();
        return Task.FromResult(ApiResult<PagedResult<AdminOrderListItemDto>>.Ok(new PagedResult<AdminOrderListItemDto>(items, query.Page, query.PageSize, all.Count)));
    }

    public Task<ApiResult<AdminOrderDto>> GetOrderAsync(Guid id, CancellationToken ct = default) => Task.FromResult(OrNotFound(store.Find(id)));

    public Task<ApiResult<AdminOrderDto>> RefundAsync(Guid id, RefundOrderRequest request, CancellationToken ct = default) => Task.FromResult(OrNotFound(store.Refund(id, request, clock)));

    public Task<ApiResult<AdminOrderDto>> ReconcileAsync(Guid id, CancellationToken ct = default) => Task.FromResult(OrNotFound(store.Find(id)));

    private static ApiResult<AdminOrderDto> OrNotFound(AdminOrderDto? order) =>
        order is null ? ApiResult<AdminOrderDto>.Fail("NOT_FOUND", "We couldn't find that. It may have been removed.", "demo-order", 404) : ApiResult<AdminOrderDto>.Ok(order);
}

public sealed class StubDevBillingApiClient(StubBillingStore store, TimeProvider clock) : IDevBillingApiClient
{
    public Task<ApiResult<bool>> SimulateAsync(Guid orderId, string outcome, CancellationToken ct = default) =>
        Task.FromResult(store.Simulate(orderId, outcome, clock) is null
            ? ApiResult<bool>.Fail("NOT_FOUND", "We couldn't find that. It may have been removed.", "demo-order", 404)
            : ApiResult<bool>.Ok(true));
}

/// <summary>Design-review stub: an empty notification feed, so the bell does not call the real API with a fake token.</summary>
public sealed class StubNotificationsApiClient : INotificationsApiClient
{
    public Task<ApiResult<NotificationFeedDto>> ListAsync(NotificationListQuery query, CancellationToken ct = default, bool background = false) =>
        Task.FromResult(ApiResult<NotificationFeedDto>.Ok(new NotificationFeedDto(0, new PagedResult<NotificationDto>([], 1, query.PageSize, 0))));

    public Task<ApiResult<bool>> MarkReadAsync(Guid id, CancellationToken ct = default) => Task.FromResult(ApiResult<bool>.Ok(true));
}
