using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Http;
using NexaVerify.Application.Billing;
using NexaVerify.Contracts.Billing;

namespace NexaVerify.Api.Controllers.Public;

/// <summary>The credit packs on the marketing pricing page. Anonymous and cacheable; an empty list while online payments are off.</summary>
[AllowAnonymous]
[Route("api/v1/public")]
public sealed class PublicBillingController : ApiControllerBase
{
    private readonly IPublicBillingService _billing;

    public PublicBillingController(IPublicBillingService billing)
    {
        _billing = billing;
    }

    [HttpGet("packs")]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType<IReadOnlyList<PublicPackDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Packs(CancellationToken cancellationToken) =>
        ToActionResult(await _billing.GetPacksAsync(cancellationToken));
}
