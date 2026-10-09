using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Filters;
using NexaVerify.Api.Http;
using NexaVerify.Application.Api;
using NexaVerify.Application.Billing;
using NexaVerify.Contracts.Billing;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;

namespace NexaVerify.Api.Controllers.Client;

/// <summary>
/// Buying credits online. The client comes from the credential (there is no client id in any route or body) and API keys can never reach
/// these endpoints: the billing permissions are not assignable to keys. The request never carries an amount: the price is the pack's.
/// </summary>
[Route("api/v1/client/billing")]
public sealed class BillingController : ApiControllerBase
{
    private const string InvoiceCsp = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; frame-ancestors 'none'";

    private readonly IBillingService _billing;

    public BillingController(IBillingService billing)
    {
        _billing = billing;
    }

    /// <summary>Whether online payments are on, and the tax and currency rules the pages should show. Answers 200 even when billing is off.</summary>
    [HttpGet("config")]
    [HasPermission(Permissions.Billing.Read)]
    [ProducesResponseType<BillingConfigDto>(StatusCodes.Status200OK)]
    public IActionResult Config() => Ok(_billing.GetConfig());

    /// <summary>Active credit packs with the tax worked out.</summary>
    [HttpGet("packs")]
    [HasPermission(Permissions.Billing.Read)]
    [ProducesResponseType<IReadOnlyList<CreditPackDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Packs(CancellationToken cancellationToken) =>
        ToActionResult(await _billing.ListPacksAsync(cancellationToken));

    /// <summary>Opens a hosted payment page for a pack. 201 with the page to send the customer to. Rate limited per user and per client.</summary>
    [HttpPost("checkout")]
    [HasPermission(Permissions.Billing.Manage)]
    [ProducesResponseType<CheckoutResponseDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Checkout(CheckoutRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _billing.CheckoutAsync(request, cancellationToken), r => Created($"/api/v1/client/billing/orders/{r.OrderId}", r));

    [HttpGet("orders")]
    [HasPermission(Permissions.Billing.Read)]
    [ProducesResponseType<PagedResult<OrderListItemDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Orders([FromQuery] OrderListQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _billing.ListOrdersAsync(query, cancellationToken));

    /// <summary>The orders as CSV (newest 10 000), neutralised against CSV injection and audit-logged.</summary>
    [HttpGet("orders/export.csv")]
    [HasPermission(Permissions.Billing.Read)]
    [Throttle(ThrottlePolicies.Exports)]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportOrders(CancellationToken cancellationToken)
    {
        var result = await _billing.ExportOrdersAsync(cancellationToken);
        return result.IsSuccess ? new CsvStreamResult(result.Value) : ProblemFor(result.Error!);
    }

    /// <summary>One order; the page to poll after the customer returns from the provider. <c>licenseId</c> appears once the payment is confirmed.</summary>
    [HttpGet("orders/{id:guid}")]
    [HasPermission(Permissions.Billing.Read)]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Order(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _billing.GetOrderAsync(id, cancellationToken));

    [HttpPost("orders/{id:guid}/cancel")]
    [HasPermission(Permissions.Billing.Manage)]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _billing.CancelOrderAsync(id, cancellationToken));

    /// <summary>The printable invoice (self-contained HTML). 409 until the order is paid.</summary>
    [HttpGet("orders/{id:guid}/invoice")]
    [HasPermission(Permissions.Billing.Read)]
    [Produces("text/html")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Invoice(Guid id, CancellationToken cancellationToken)
    {
        var result = await _billing.GetInvoiceAsync(id, cancellationToken);
        if (result.IsFailure)
        {
            return ProblemFor(result.Error!);
        }

        // The invoice needs inline styles; this page gets its own, still very strict, policy (the security-headers middleware leaves it alone).
        Response.Headers.ContentSecurityPolicy = InvoiceCsp;
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.ContentDisposition = $"inline; filename=\"{result.Value.FileName}\"";
        return Content(result.Value.Html, "text/html; charset=utf-8");
    }

    [HttpGet("profile")]
    [HasPermission(Permissions.Billing.Read)]
    [ProducesResponseType<BillingProfileDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Profile(CancellationToken cancellationToken) =>
        ToActionResult(await _billing.GetProfileAsync(cancellationToken));

    [HttpPut("profile")]
    [HasPermission(Permissions.Billing.Manage)]
    [ProducesResponseType<BillingProfileDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateProfile(UpdateBillingProfileRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _billing.UpdateProfileAsync(request, cancellationToken));
}
