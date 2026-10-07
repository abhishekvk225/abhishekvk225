using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Http;
using NexaVerify.Application.Tenancy;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Tenancy;

namespace NexaVerify.Api.Controllers.Client;

/// <summary>
/// The client's own account. There is deliberately no client id anywhere in these routes: the tenant is always the caller's,
/// taken from the credential.
/// </summary>
[Route("api/v1/client")]
public sealed class ClientPortalController : ApiControllerBase
{
    private static readonly string[] Recognition = ["Recognition"];
    private static readonly string[] Notifications = ["Notifications"];
    private static readonly string[] Security = ["Security", "Integration"];

    private readonly IClientPortalService _portal;

    public ClientPortalController(IClientPortalService portal)
    {
        _portal = portal;
    }

    [HttpGet("profile")]
    [HasPermission(Permissions.ClientProfile.Read)]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken) =>
        ToActionResult(await _portal.GetProfileAsync(cancellationToken));

    [HttpPut("profile")]
    [HasPermission(Permissions.ClientProfile.Update)]
    public async Task<IActionResult> UpdateProfile(UpdateClientProfileRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _portal.UpdateProfileAsync(request, cancellationToken));

    [HttpGet("users")]
    [HasPermission(Permissions.Users.Manage)]
    public async Task<IActionResult> ListUsers([FromQuery] PageRequest query, CancellationToken cancellationToken) =>
        ToActionResult(await _portal.ListUsersAsync(query, cancellationToken));

    [HttpPost("users")]
    [HasPermission(Permissions.Users.Manage)]
    [ProducesResponseType<ClientUserDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateUser(CreateClientUserRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _portal.CreateUserAsync(request, cancellationToken), u => Created($"/api/v1/client/users/{u.Id}", u));

    [HttpPut("users/{userId:guid}")]
    [HasPermission(Permissions.Users.Manage)]
    public async Task<IActionResult> UpdateUser(Guid userId, UpdateClientUserRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _portal.UpdateUserAsync(userId, request, cancellationToken));

    [HttpPost("users/{userId:guid}/reset-password")]
    [HasPermission(Permissions.Users.Manage)]
    public async Task<IActionResult> ResetUserPassword(Guid userId, CancellationToken cancellationToken) =>
        ToActionResult(await _portal.ResetUserPasswordAsync(userId, cancellationToken), Accepted);

    [HttpGet("audit-logs")]
    [HasPermission(Permissions.Audit.ReadClient)]
    public async Task<IActionResult> AuditLogs([FromQuery] ActivityQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _portal.GetActivityAsync(query, cancellationToken));

    [HttpGet("logins")]
    [HasPermission(Permissions.Audit.ReadClient)]
    public async Task<IActionResult> Logins([FromQuery] ActivityQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _portal.GetLoginsAsync(query, cancellationToken));

    [HttpGet("settings")]
    [HasPermission(Permissions.ClientProfile.Read)]
    public async Task<IActionResult> AllSettings(CancellationToken cancellationToken) =>
        ToActionResult(await _portal.GetSettingsAsync(null, cancellationToken));

    [HttpGet("settings/recognition")]
    [HasPermission(Permissions.Settings.Recognition)]
    public async Task<IActionResult> GetRecognition(CancellationToken cancellationToken) =>
        ToActionResult(await _portal.GetSettingsAsync(Recognition, cancellationToken));

    [HttpPut("settings/recognition")]
    [HasPermission(Permissions.Settings.Recognition)]
    public async Task<IActionResult> UpdateRecognition(UpdateSettingsRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _portal.UpdateSettingsAsync(request, Recognition, cancellationToken));

    [HttpGet("settings/notifications")]
    [HasPermission(Permissions.Settings.Notifications)]
    public async Task<IActionResult> GetNotifications(CancellationToken cancellationToken) =>
        ToActionResult(await _portal.GetSettingsAsync(Notifications, cancellationToken));

    [HttpPut("settings/notifications")]
    [HasPermission(Permissions.Settings.Notifications)]
    public async Task<IActionResult> UpdateNotifications(UpdateSettingsRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _portal.UpdateSettingsAsync(request, Notifications, cancellationToken));

    [HttpGet("settings/security")]
    [HasPermission(Permissions.Settings.Security)]
    public async Task<IActionResult> GetSecurity(CancellationToken cancellationToken) =>
        ToActionResult(await _portal.GetSettingsAsync(Security, cancellationToken));

    [HttpPut("settings/security")]
    [HasPermission(Permissions.Settings.Security)]
    public async Task<IActionResult> UpdateSecurity(UpdateSettingsRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _portal.UpdateSettingsAsync(request, Security, cancellationToken));
}
