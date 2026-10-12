using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Filters;
using NexaVerify.Api.Http;
using NexaVerify.Application.Api;
using NexaVerify.Application.Verification.Admin;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Verification;

namespace NexaVerify.Api.Controllers.Verification;

/// <summary>
/// Platform administration of document verification: module availability, provider configuration status (never a secret), statistics and
/// tenant-scoped operational investigation. Platform staff see request METADATA only: never documents, extracted values, masked
/// identifiers or check details (those tables are not readable in platform scope).
/// </summary>
[Route("api/v1/admin")]
public sealed class VerificationAdminController : ApiControllerBase
{
    private readonly IVerificationAdminService _admin;

    public VerificationAdminController(IVerificationAdminService admin)
    {
        _admin = admin;
    }

    /// <summary>All modules (Upcoming ones too), effective status, provider configured flags with 24 h outcome counts, resolved platform credit costs.</summary>
    [HttpGet("verifications/modules")]
    [HasPermission(Permissions.VerificationAdmin.Read)]
    [ProducesResponseType<IReadOnlyList<AdminVerificationModuleDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Modules(CancellationToken cancellationToken) =>
        ToActionResult(await _admin.ListModulesAsync(cancellationToken));

    /// <summary>Switches an Available module on or off (reason required, audited). Upcoming and unknown keys are 404. Submissions to a disabled module answer 404; requests already accepted still finish.</summary>
    [HttpPut("verifications/modules/{key}")]
    [HasPermission(Permissions.VerificationAdmin.Configure)]
    [ProducesResponseType<AdminVerificationModuleDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateModule(string key, UpdateVerificationModuleRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _admin.UpdateModuleAsync(key, request, cancellationToken));

    /// <summary>Volumes, review and failure counts, provider outcomes, credits by stage, p50/p95 processing time, queue depth.</summary>
    [HttpGet("verifications/stats")]
    [HasPermission(Permissions.VerificationAdmin.Read)]
    [Throttle(ThrottlePolicies.Dashboards)]
    [ProducesResponseType<VerificationStatsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Stats([FromQuery] VerificationStatsQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _admin.GetStatsAsync(query, cancellationToken));

    /// <summary>The same figures per day and type as CSV (neutralised against CSV injection, audited).</summary>
    [HttpGet("verifications/stats/export.csv")]
    [HasPermission(Permissions.VerificationAdmin.Read)]
    [Throttle(ThrottlePolicies.Exports)]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> StatsCsv([FromQuery] VerificationStatsQuery query, CancellationToken cancellationToken)
    {
        var result = await _admin.ExportStatsAsync(query, cancellationToken);
        return result.IsSuccess ? new CsvStreamResult(result.Value) : ProblemFor(result.Error!);
    }

    /// <summary>One client's verification requests (metadata only).</summary>
    [HttpGet("clients/{clientId:guid}/verifications")]
    [HasPermission(Permissions.VerificationAdmin.Read)]
    [ProducesResponseType<PagedResult<AdminVerificationItemDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClientRequests(Guid clientId, [FromQuery] AdminVerificationListQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _admin.ListClientRequestsAsync(clientId, query, cancellationToken));

    /// <summary>One request's metadata and transition events. Every view is audited.</summary>
    [HttpGet("clients/{clientId:guid}/verifications/{id:guid}")]
    [HasPermission(Permissions.VerificationAdmin.Read)]
    [ProducesResponseType<AdminVerificationDetailDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClientRequest(Guid clientId, Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _admin.GetClientRequestAsync(clientId, id, cancellationToken));

    /// <summary>Platform audit history of verification administration (module switches, administrative views).</summary>
    [HttpGet("verifications/audit")]
    [HasPermission(Permissions.Audit.Read)]
    [ProducesResponseType<PagedResult<AdminVerificationAuditItemDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Audit([FromQuery] AdminVerificationAuditQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _admin.ListAuditAsync(query, cancellationToken));
}
