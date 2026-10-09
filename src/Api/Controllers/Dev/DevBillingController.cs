using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Http;
using NexaVerify.Application.Billing;
using NexaVerify.Contracts.Billing;
using NexaVerify.Contracts.Identity;

namespace NexaVerify.Api.Controllers.Dev;

/// <summary>
/// Plays the payment provider for the simulated provider (Billing:Provider=Simulated). The controller is REMOVED from the application
/// model unless the host runs in Development or Testing (<see cref="DevelopmentOnlyAttribute"/>), so in any other environment the route
/// does not exist. It still needs a signed-in client user with <c>billing.manage</c>, and only sees that client's own orders.
/// </summary>
[DevelopmentOnly]
[Route("api/v1/dev/billing")]
public sealed class DevBillingController : ApiControllerBase
{
    private readonly IBillingService _billing;

    public DevBillingController(IBillingService billing)
    {
        _billing = billing;
    }

    [HttpPost("simulate/{orderId:guid}")]
    [HasPermission(Permissions.Billing.Manage)]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Simulate(Guid orderId, SimulatePaymentRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _billing.SimulateAsync(orderId, request, cancellationToken));
}
