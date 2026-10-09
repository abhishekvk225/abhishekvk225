using System.Globalization;
using NexaVerify.Contracts.Common;

namespace NexaVerify.Web.Services;

/// <summary>Platform staff billing (<c>/admin/billing</c>): credit packs, orders, refunds, reconciliation.</summary>
public interface IAdminBillingApiClient
{
    Task<ApiResult<IReadOnlyList<AdminPackDto>>> ListPacksAsync(CancellationToken ct = default);

    Task<ApiResult<AdminPackDto>> CreatePackAsync(SaveAdminPackRequest request, CancellationToken ct = default);

    Task<ApiResult<AdminPackDto>> UpdatePackAsync(Guid id, SaveAdminPackRequest request, CancellationToken ct = default);

    Task<ApiResult<PagedResult<AdminOrderListItemDto>>> ListOrdersAsync(AdminOrderQuery query, CancellationToken ct = default);

    Task<ApiResult<AdminOrderDto>> GetOrderAsync(Guid id, CancellationToken ct = default);

    /// <summary>Refund a paid order. A null amount refunds what is left.</summary>
    Task<ApiResult<AdminOrderDto>> RefundAsync(Guid id, RefundOrderRequest request, CancellationToken ct = default);

    /// <summary>Asks the payment partner what really happened and brings the order in line.</summary>
    Task<ApiResult<AdminOrderDto>> ReconcileAsync(Guid id, CancellationToken ct = default);
}

public sealed class AdminBillingApiClient(IApiGateway api) : IAdminBillingApiClient
{
    public Task<ApiResult<IReadOnlyList<AdminPackDto>>> ListPacksAsync(CancellationToken ct = default) =>
        api.GetAsync<IReadOnlyList<AdminPackDto>>("admin/billing/packs", ct);

    public Task<ApiResult<AdminPackDto>> CreatePackAsync(SaveAdminPackRequest request, CancellationToken ct = default) =>
        api.SendAsync<AdminPackDto>(HttpMethod.Post, "admin/billing/packs", request, ct);

    public Task<ApiResult<AdminPackDto>> UpdatePackAsync(Guid id, SaveAdminPackRequest request, CancellationToken ct = default) =>
        api.SendAsync<AdminPackDto>(HttpMethod.Put, $"admin/billing/packs/{id}", request, ct);

    public Task<ApiResult<PagedResult<AdminOrderListItemDto>>> ListOrdersAsync(AdminOrderQuery query, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<AdminOrderListItemDto>>(
            ApiQuery.With("admin/billing/orders", ("clientId", query.ClientId), ("status", query.Status),
                ("from", query.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("to", query.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("page", query.Page), ("pageSize", query.PageSize)), ct);

    public async Task<ApiResult<AdminOrderDto>> GetOrderAsync(Guid id, CancellationToken ct = default) =>
        Flatten(await api.GetAsync<AdminOrderWire>($"admin/billing/orders/{id}", ct));

    public async Task<ApiResult<AdminOrderDto>> RefundAsync(Guid id, RefundOrderRequest request, CancellationToken ct = default) =>
        Flatten(await api.SendAsync<AdminOrderWire>(HttpMethod.Post, $"admin/billing/orders/{id}/refund", request, ct));

    public async Task<ApiResult<AdminOrderDto>> ReconcileAsync(Guid id, CancellationToken ct = default) =>
        Flatten(await api.SendAsync<AdminOrderWire>(HttpMethod.Post, $"admin/billing/orders/{id}/reconcile", null, ct));

    /// <summary>The API nests the order fields under <c>order</c>; the screens use one flat model.</summary>
    private static ApiResult<AdminOrderDto> Flatten(ApiResult<AdminOrderWire> result)
    {
        if (!result.IsSuccess)
        {
            return ApiResult<AdminOrderDto>.Fail(result.Error!);
        }

        var w = result.Value;
        var o = w.Order;
        var refunds = (w.Refunds ?? []).Select(r => new RefundDto(r.Id, r.AmountMinor, r.CreditsRevoked, r.Reason, r.CreatedAt)).ToList();
        return ApiResult<AdminOrderDto>.Ok(new AdminOrderDto(
            o.Id, o.InvoiceNumber, w.ClientId, w.ClientName, o.PackName, o.Credits, o.ValidityDays, o.SubtotalMinor, o.TaxMinor, o.TotalMinor,
            o.TaxPercent, o.TaxLabel, o.Currency, o.Status, o.CreatedAt, o.PaidAt, o.LicenseId, w.Provider, w.ProviderPaymentId, refunds, null));
    }

    private sealed record AdminOrderWire(OrderDto Order, Guid ClientId, string? ClientName, string? Provider, string? ProviderPaymentId, IReadOnlyList<RefundWire>? Refunds);

    private sealed record RefundWire(Guid Id, long AmountMinor, int CreditsRevoked, string? Reason, DateTimeOffset CreatedAt);
}
