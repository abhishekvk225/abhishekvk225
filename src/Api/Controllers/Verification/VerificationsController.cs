using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Filters;
using NexaVerify.Api.Http;
using NexaVerify.Application.Api;
using NexaVerify.Application.Verification.Query;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Verification;

namespace NexaVerify.Api.Controllers.Verification;

/// <summary>
/// Reads and manages the calling client's verifications: module catalogue, history, status, result, transactions, cancel, provider-check
/// retry, erasure and short-lived report downloads. Another client's id is always 404. Results never state that a document is genuine;
/// only <c>external.state = Verified</c> with a provider reference means officially verified.
/// </summary>
[Route("api/v1/verifications")]
public sealed class VerificationsController : ApiControllerBase
{
    private readonly IVerificationQueryService _query;
    private readonly IVerificationLifecycleService _lifecycle;
    private readonly IVerificationDownloadService _downloads;

    public VerificationsController(IVerificationQueryService query, IVerificationLifecycleService lifecycle, IVerificationDownloadService downloads)
    {
        _query = query;
        _lifecycle = lifecycle;
        _downloads = downloads;
    }

    /// <summary>Available and Upcoming modules. Upcoming modules are catalogue entries only: nothing can be submitted or charged for them.</summary>
    [HttpGet("modules")]
    [HasPermission(Permissions.Verification.Read)]
    [ProducesResponseType<IReadOnlyList<VerificationModuleDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Modules(CancellationToken cancellationToken) =>
        ToActionResult(await _query.ListModulesAsync(cancellationToken));

    /// <summary>History with filters (type, status, reviewRequired, externalState, from, to, search) and paging.</summary>
    [HttpGet]
    [HasPermission(Permissions.Verification.Read)]
    [ProducesResponseType<PagedResult<VerificationListItemDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] VerificationListQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _query.ListAsync(query, cancellationToken));

    /// <summary>The full result: independent stage flags, checks, review reasons, field outcomes, provider outcome, masked extracted data.</summary>
    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Verification.Read)]
    [ProducesResponseType<VerificationResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _query.GetResultAsync(id, cancellationToken));

    /// <summary>Cheap polling target.</summary>
    [HttpGet("{id:guid}/status")]
    [HasPermission(Permissions.Verification.Read)]
    [ProducesResponseType<VerificationStatusDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Status(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _query.GetStatusAsync(id, cancellationToken));

    /// <summary>Bank statement transactions, paged (up to 500 per page).</summary>
    [HttpGet("{id:guid}/transactions")]
    [HasPermission(Permissions.Verification.Read)]
    [ProducesResponseType<PagedResult<BankTransactionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Transactions(Guid id, [FromQuery] VerificationTransactionsQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _query.GetTransactionsAsync(id, query, cancellationToken));

    /// <summary>Cancels a queued request at once, or asks a running one to stop. Needs the submit permission of the request's type.</summary>
    [HttpPost("{id:guid}/cancel")]
    [HasPermission(Permissions.Verification.Read)]
    [ProducesResponseType<VerificationStatusDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _lifecycle.CancelAsync(id, cancellationToken));

    /// <summary>Re-runs only the authoritative provider check of a finished request (not for definitive answers). Needs the submit permission of the type.</summary>
    [HttpPost("{id:guid}/retry-external")]
    [HasPermission(Permissions.Verification.Read)]
    [Throttle(ThrottlePolicies.VerificationSubmit)]
    [ProducesResponseType<VerificationAcceptedDto>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> RetryExternal(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _lifecycle.RetryExternalAsync(id, cancellationToken), accepted => Accepted(accepted.StatusUrl, accepted));

    /// <summary>Erases the document, extracted data and checks immediately (the request record and its billing trace stay).</summary>
    [HttpDelete("{id:guid}/data")]
    [HasPermission(Permissions.Verification.Erase)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Erase(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _lifecycle.EraseAsync(id, cancellationToken));

    /// <summary>Issues a single-use download link (valid for seconds) for a report, the transactions CSV or, when enabled, the original document.</summary>
    [HttpPost("{id:guid}/downloads")]
    [HasPermission(Permissions.Verification.ReportDownload)]
    [Throttle(ThrottlePolicies.VerificationDownload)]
    [ProducesResponseType<DownloadLinkDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateDownload(Guid id, VerificationDownloadRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _downloads.CreateLinkAsync(id, request, cancellationToken));

    /// <summary>Redeems a download link. Still requires authentication; the link only works for the principal it was issued to, once.</summary>
    [HttpGet("downloads/{token}")]
    [HasPermission(Permissions.Verification.Read)]
    [Throttle(ThrottlePolicies.VerificationDownload)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Download(string token, CancellationToken cancellationToken)
    {
        var result = await _downloads.RedeemAsync(token, cancellationToken);
        if (result.IsFailure)
        {
            return ProblemFor(result.Error!);
        }

        var file = result.Value;
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";
        return File(file.Content, file.ContentType, file.FileName);
    }
}
