using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Http;
using NexaVerify.Application.Public;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Public;

namespace NexaVerify.Api.Controllers.Admin;

/// <summary>Messages sent through the public contact form (newest first).</summary>
[Route("api/v1/admin/contact-requests")]
public sealed class ContactRequestsController : ApiControllerBase
{
    private readonly IContactService _contact;

    public ContactRequestsController(IContactService contact)
    {
        _contact = contact;
    }

    [HttpGet]
    [HasPermission(Permissions.Clients.Read)]
    [ProducesResponseType<PagedResult<ContactRequestDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] PageRequest query, CancellationToken cancellationToken) =>
        ToActionResult(await _contact.ListAsync(query, cancellationToken));
}
