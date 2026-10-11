using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Configuration;
using NexaVerify.Api.Http;
using NexaVerify.Application.Messaging;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Messaging;

namespace NexaVerify.Api.Controllers.Admin;

/// <summary>Platform staff: the outgoing-email outbox, retries, the suppression list and a test message. Message bodies are never exposed.</summary>
[Route("api/v1/admin/email")]
public sealed class EmailAdminController : ApiControllerBase
{
    private readonly IEmailAdminService _email;

    public EmailAdminController(IEmailAdminService email)
    {
        _email = email;
    }

    /// <summary>
    /// Outgoing mail, newest first. Filters: <c>status</c> (Pending, Sending, Sent, Failed, Abandoned), <c>template</c>, <c>recipient</c>
    /// (exact address, or the start of one), <c>from</c> / <c>to</c> (UTC creation time). Recipient addresses are masked unless the caller holds <c>email.read</c>.
    /// </summary>
    [HttpGet("outbox")]
    [HasPermission(Permissions.System.Configure)]
    [ProducesResponseType<PagedResult<EmailOutboxItemDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Outbox([FromQuery] EmailOutboxQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _email.ListOutboxAsync(query, cancellationToken));

    /// <summary>Queues a failed or abandoned message again with a fresh set of attempts. 409 when it is in another state or its content was already erased.</summary>
    [HttpPost("outbox/{id:guid}/retry")]
    [HasPermission(Permissions.Email.Manage)]
    [ProducesResponseType<EmailOutboxItemDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _email.RetryAsync(id, cancellationToken));

    /// <summary>Addresses mail is no longer sent to because the mail server permanently refused them.</summary>
    [HttpGet("suppressions")]
    [HasPermission(Permissions.Email.Read)]
    [ProducesResponseType<PagedResult<EmailSuppressionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Suppressions([FromQuery] PageRequest page, CancellationToken cancellationToken) =>
        ToActionResult(await _email.ListSuppressionsAsync(page, cancellationToken));

    /// <summary>Removes an address from the suppression list, so mail to it is attempted again.</summary>
    [HttpDelete("suppressions/{id:guid}")]
    [HasPermission(Permissions.Email.Manage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveSuppression(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _email.RemoveSuppressionAsync(id, cancellationToken));

    /// <summary>Queues a test message to an address (rate limited, audited). Check the outbox for the delivery result.</summary>
    [HttpPost("test")]
    [HasPermission(Permissions.Email.Manage)]
    [EnableRateLimiting(RateLimitSettings.EmailTestPolicy)]
    [ProducesResponseType<SendTestEmailResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Test(SendTestEmailRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _email.SendTestAsync(request, cancellationToken), r => Accepted(r));
}
