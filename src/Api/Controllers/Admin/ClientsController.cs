using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Http;
using NexaVerify.Application.Tenancy;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Tenancy;

namespace NexaVerify.Api.Controllers.Admin;

/// <summary>Platform-side client management. The client is addressed in the route and authorised by permission.</summary>
[Route("api/v1/admin/clients")]
public sealed class ClientsController : ApiControllerBase
{
    private readonly IClientService _clients;
    private readonly IClientSettingsService _settings;

    public ClientsController(IClientService clients, IClientSettingsService settings)
    {
        _clients = clients;
        _settings = settings;
    }

    [HttpGet]
    [HasPermission(Permissions.Clients.Read)]
    public async Task<IActionResult> List([FromQuery] ClientListQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _clients.ListAsync(query, cancellationToken));

    [HttpPost]
    [HasPermission(Permissions.Clients.Create)]
    [ProducesResponseType<ClientDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateClientRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _clients.CreateAsync(request, cancellationToken), c => Created($"/api/v1/admin/clients/{c.Id}", c));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Clients.Read)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _clients.GetAsync(id, cancellationToken));

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.Clients.Update)]
    public async Task<IActionResult> Update(Guid id, UpdateClientRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _clients.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/activate")]
    [HasPermission(Permissions.Clients.ManageStatus)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _clients.ActivateAsync(id, cancellationToken));

    [HttpPost("{id:guid}/deactivate")]
    [HasPermission(Permissions.Clients.ManageStatus)]
    public async Task<IActionResult> Deactivate(Guid id, ClientStatusRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _clients.DeactivateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/suspend")]
    [HasPermission(Permissions.Clients.ManageStatus)]
    public async Task<IActionResult> Suspend(Guid id, ClientStatusRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _clients.SuspendAsync(id, request, cancellationToken));

    [HttpGet("{id:guid}/users")]
    [HasPermission(Permissions.Clients.Read)]
    public async Task<IActionResult> Users(Guid id, [FromQuery] PageRequest query, CancellationToken cancellationToken) =>
        ToActionResult(await _clients.ListUsersAsync(id, query, cancellationToken));

    [HttpPost("{id:guid}/users/{userId:guid}/reset-password")]
    [HasPermission(Permissions.Clients.ResetPassword)]
    public async Task<IActionResult> ResetPassword(Guid id, Guid userId, CancellationToken cancellationToken) =>
        ToActionResult(await _clients.ResetUserPasswordAsync(id, userId, cancellationToken), Accepted);

    [HttpGet("{id:guid}/activity")]
    [HasPermission(Permissions.Audit.Read)]
    public async Task<IActionResult> Activity(Guid id, [FromQuery] ActivityQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _clients.GetActivityAsync(id, query, cancellationToken));

    [HttpGet("{id:guid}/logins")]
    [HasPermission(Permissions.Audit.Read)]
    public async Task<IActionResult> Logins(Guid id, [FromQuery] ActivityQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _clients.GetLoginsAsync(id, query, cancellationToken));

    [HttpGet("{id:guid}/settings")]
    [HasPermission(Permissions.Clients.Settings)]
    public async Task<IActionResult> GetSettings(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _settings.GetAsync(id, null, cancellationToken));

    [HttpPut("{id:guid}/settings")]
    [HasPermission(Permissions.Clients.Settings)]
    public async Task<IActionResult> UpdateSettings(Guid id, UpdateSettingsRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _settings.UpdateAsync(id, request, null, cancellationToken));
}
