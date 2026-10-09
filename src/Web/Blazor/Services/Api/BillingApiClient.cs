using System.Net;
using NexaVerify.Contracts.Common;

namespace NexaVerify.Web.Services;

/// <summary>A client's own billing (<c>/client/billing</c>). The client always comes from the credential; amounts always come from the API.</summary>
public interface IBillingApiClient
{
    Task<ApiResult<IReadOnlyList<CreditPackDto>>> GetPacksAsync(CancellationToken ct = default);

    /// <summary>Creates an order and returns the payment partner's page to send the person to. Same <paramref name="idempotencyKey"/> = same order.</summary>
    Task<ApiResult<CheckoutResponse>> CheckoutAsync(Guid packId, string idempotencyKey, CancellationToken ct = default);

    Task<ApiResult<PagedResult<OrderListItemDto>>> ListOrdersAsync(PageRequest page, string? status, CancellationToken ct = default);

    /// <summary><paramref name="passive"/>: a background poll that must not keep the sign-in session alive.</summary>
    Task<ApiResult<OrderDto>> GetOrderAsync(Guid id, CancellationToken ct = default, bool passive = false);

    /// <summary>The saved billing details; a client that never saved any gets an empty profile (404 is not an error here).</summary>
    Task<ApiResult<BillingProfileDto>> GetProfileAsync(CancellationToken ct = default);

    Task<ApiResult<BillingProfileDto>> SaveProfileAsync(SaveBillingProfileRequest request, CancellationToken ct = default);
}

public sealed class BillingApiClient(IApiGateway api) : IBillingApiClient
{
    public const string IdempotencyHeader = "Idempotency-Key";

    public Task<ApiResult<IReadOnlyList<CreditPackDto>>> GetPacksAsync(CancellationToken ct = default) =>
        api.GetAsync<IReadOnlyList<CreditPackDto>>("client/billing/packs", ct);

    public Task<ApiResult<CheckoutResponse>> CheckoutAsync(Guid packId, string idempotencyKey, CancellationToken ct = default) =>
        api.SendAsync<CheckoutResponse>(HttpMethod.Post, "client/billing/checkout", new CheckoutRequest(packId, idempotencyKey), ct,
            new ApiCallOptions { Headers = new Dictionary<string, string> { [IdempotencyHeader] = idempotencyKey } });

    public Task<ApiResult<PagedResult<OrderListItemDto>>> ListOrdersAsync(PageRequest page, string? status, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<OrderListItemDto>>(ApiQuery.With("client/billing/orders", ("page", page.Page), ("pageSize", page.PageSize), ("status", status)), ct);

    public Task<ApiResult<OrderDto>> GetOrderAsync(Guid id, CancellationToken ct = default, bool passive = false) =>
        api.GetAsync<OrderDto>($"client/billing/orders/{id}", ct, passive ? new ApiCallOptions { Passive = true } : null);

    public async Task<ApiResult<BillingProfileDto>> GetProfileAsync(CancellationToken ct = default)
    {
        var result = await api.GetAsync<BillingProfileDto>("client/billing/profile", ct);
        return !result.IsSuccess && result.Error!.Status == (int)HttpStatusCode.NotFound
            ? ApiResult<BillingProfileDto>.Ok(new BillingProfileDto(null, null, null, null, null, null, null, null, null, null))
            : result;
    }

    public async Task<ApiResult<BillingProfileDto>> SaveProfileAsync(SaveBillingProfileRequest request, CancellationToken ct = default)
    {
        // The reply body is not relied on (200 with the profile or 204 both work): read the saved profile back, which also brings the new row version.
        var saved = await api.SendAsync(HttpMethod.Put, "client/billing/profile", request, ct);
        return saved.IsSuccess ? await GetProfileAsync(ct) : ApiResult<BillingProfileDto>.Fail(saved.Error!);
    }
}

/// <summary>Development-only payment simulator (<c>POST /dev/billing/simulate/{orderId}</c>). Never registered outside Development/Testing.</summary>
public interface IDevBillingApiClient
{
    /// <summary><paramref name="outcome"/>: <c>success</c>, <c>failed</c> or <c>cancelled</c>.</summary>
    Task<ApiResult<bool>> SimulateAsync(Guid orderId, string outcome, CancellationToken ct = default);
}

public sealed class DevBillingApiClient(IApiGateway api) : IDevBillingApiClient
{
    public Task<ApiResult<bool>> SimulateAsync(Guid orderId, string outcome, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"dev/billing/simulate/{orderId}", new { outcome }, ct);
}
