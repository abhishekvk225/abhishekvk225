using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Http;
using NexaVerify.Application.Billing;
using NexaVerify.Contracts.Billing;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;

namespace NexaVerify.Api.Controllers.Admin;

/// <summary>Platform staff: the credit-pack catalogue, every client's orders, refunds and manual reconciliation.</summary>
[Route("api/v1/admin/billing")]
public sealed class BillingAdminController : ApiControllerBase
{
    private readonly IBillingAdminService _billing;

    public BillingAdminController(IBillingAdminService billing)
    {
        _billing = billing;
    }

    /// <summary>Provider, sandbox/live mode, tax and currencies. Never any key material. Platform staff only.</summary>
    [HttpGet("config")]
    [HasPermission(Permissions.Billing.OrdersRead)]
    [ProducesResponseType<AdminBillingConfigDto>(StatusCodes.Status200OK)]
    public IActionResult Config() => Ok(_billing.GetConfig());

    [HttpGet("packs")]
    [HasPermission(Permissions.Billing.PacksManage)]
    [ProducesResponseType<IReadOnlyList<AdminCreditPackDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Packs(CancellationToken cancellationToken) =>
        ToActionResult(await _billing.ListPacksAsync(cancellationToken));

    [HttpGet("packs/{id:guid}")]
    [HasPermission(Permissions.Billing.PacksManage)]
    [ProducesResponseType<AdminCreditPackDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Pack(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _billing.GetPackAsync(id, cancellationToken));

    [HttpPost("packs")]
    [HasPermission(Permissions.Billing.PacksManage)]
    [ProducesResponseType<AdminCreditPackDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreatePack(CreateCreditPackRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _billing.CreatePackAsync(request, cancellationToken), p => Created($"/api/v1/admin/billing/packs/{p.Id}", p));

    /// <summary>Changes a pack. Orders already placed keep the price they were shown.</summary>
    [HttpPut("packs/{id:guid}")]
    [HasPermission(Permissions.Billing.PacksManage)]
    [ProducesResponseType<AdminCreditPackDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdatePack(Guid id, UpdateCreditPackRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _billing.UpdatePackAsync(id, request, cancellationToken));

    /// <summary>Deletes a pack nobody ordered; 409 otherwise (switch it off instead).</summary>
    [HttpDelete("packs/{id:guid}")]
    [HasPermission(Permissions.Billing.PacksManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeletePack(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _billing.DeletePackAsync(id, cancellationToken));

    [HttpGet("orders")]
    [HasPermission(Permissions.Billing.OrdersRead)]
    [ProducesResponseType<PagedResult<AdminOrderListItemDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Orders([FromQuery] AdminOrderQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _billing.ListOrdersAsync(query, cancellationToken));

    [HttpGet("orders/{id:guid}")]
    [HasPermission(Permissions.Billing.OrdersRead)]
    [ProducesResponseType<AdminOrderDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Order(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _billing.GetOrderAsync(id, cancellationToken));

    /// <summary>
    /// Refunds all or part of a paid order through the payment provider and takes back the matching share of the UNUSED credits of the
    /// top-up license (never below zero). Audited; emits <c>license.credits_revoked</c>.
    /// </summary>
    [HttpPost("orders/{id:guid}/refund")]
    [HasPermission(Permissions.Billing.Refund)]
    [ProducesResponseType<AdminOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Refund(Guid id, RefundOrderRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _billing.RefundAsync(id, request, cancellationToken));

    /// <summary>Asks the payment provider what became of the order and applies the answer (grants the credits if it was paid). Safe to repeat.</summary>
    [HttpPost("orders/{id:guid}/reconcile")]
    [HasPermission(Permissions.Billing.Refund)]
    [ProducesResponseType<AdminOrderDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reconcile(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _billing.ReconcileAsync(id, cancellationToken));
}
