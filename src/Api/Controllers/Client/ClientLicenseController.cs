using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Http;
using NexaVerify.Application.Licensing;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;

namespace NexaVerify.Api.Controllers.Client;

/// <summary>A client's read-only view of its own licenses and credit history. The client comes from the credential.</summary>
[Route("api/v1/client/licenses")]
public sealed class ClientLicenseController : ApiControllerBase
{
    private readonly IClientLicenseService _licenses;

    public ClientLicenseController(IClientLicenseService licenses)
    {
        _licenses = licenses;
    }

    [HttpGet("summary")]
    [HasPermission(Permissions.LicenseView.Read)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        ToActionResult(await _licenses.GetSummaryAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.LicenseView.Read)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _licenses.GetAsync(id, cancellationToken));

    [HttpGet("{id:guid}/transactions")]
    [HasPermission(Permissions.LicenseView.Read)]
    public async Task<IActionResult> Transactions(Guid id, [FromQuery] PageRequest query, CancellationToken cancellationToken) =>
        ToActionResult(await _licenses.GetTransactionsAsync(id, query, cancellationToken));
}
