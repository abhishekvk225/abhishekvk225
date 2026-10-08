using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Http;
using NexaVerify.Application.Identity;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;

namespace NexaVerify.Api.Controllers.Admin;

[Route("api/v1/admin/roles")]
[HasPermission(Permissions.RolesAdmin.Manage)]
public sealed class RolesController : ApiControllerBase
{
    private readonly IRoleService _roles;

    public RolesController(IRoleService roles)
    {
        _roles = roles;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        ToActionResult(await _roles.ListRolesAsync(cancellationToken));

    [HttpPost]
    [ProducesResponseType<RoleDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateRoleRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _roles.CreateRoleAsync(request, cancellationToken), role => Created($"/api/v1/admin/roles/{role.Id}", role));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateRoleRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _roles.UpdateRoleAsync(id, request, cancellationToken));
}

[Route("api/v1/admin/permissions")]
[HasPermission(Permissions.RolesAdmin.Manage)]
public sealed class PermissionsController : ApiControllerBase
{
    private readonly IRoleService _roles;

    public PermissionsController(IRoleService roles)
    {
        _roles = roles;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        ToActionResult(await _roles.ListPermissionsAsync(cancellationToken));
}

[Route("api/v1/admin/users")]
[HasPermission(Permissions.Users.PlatformManage)]
public sealed class PlatformUsersController : ApiControllerBase
{
    private readonly IPlatformUserService _users;

    public PlatformUsersController(IPlatformUserService users)
    {
        _users = users;
    }

    [HttpGet]
    // The parameter must not be called "page": it would collide with PageRequest.Page and nothing would bind.
    public async Task<IActionResult> List([FromQuery] PageRequest query, CancellationToken cancellationToken) =>
        ToActionResult(await _users.ListAsync(query, cancellationToken));

    [HttpPost]
    [ProducesResponseType<PlatformUserDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreatePlatformUserRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _users.CreateAsync(request, cancellationToken), user => Created($"/api/v1/admin/users/{user.Id}", user));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdatePlatformUserRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _users.UpdateAsync(id, request, cancellationToken));
}

/// <summary>Resetting someone's two-factor authentication (lost device). Only another Super Admin can; the reason is audited.</summary>
[Route("api/v1/admin")]
public sealed class MfaAdminController : ApiControllerBase
{
    private readonly IMfaService _mfa;

    public MfaAdminController(IMfaService mfa)
    {
        _mfa = mfa;
    }

    [HttpPost("users/{id:guid}/mfa/reset")]
    [HasPermission(Permissions.Users.MfaReset)]
    public async Task<IActionResult> ResetStaff(Guid id, ResetMfaRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _mfa.ResetAsync(null, id, request, cancellationToken));

    [HttpPost("clients/{clientId:guid}/users/{userId:guid}/mfa/reset")]
    [HasPermission(Permissions.Users.MfaReset)]
    public async Task<IActionResult> ResetClientUser(Guid clientId, Guid userId, ResetMfaRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _mfa.ResetAsync(clientId, userId, request, cancellationToken));
}
