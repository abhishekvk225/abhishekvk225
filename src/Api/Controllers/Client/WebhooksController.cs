using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Http;
using NexaVerify.Application.Api;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;

namespace NexaVerify.Api.Controllers.Client;

/// <summary>A client registers HTTPS endpoints to receive signed events. The signing secret appears once, when created or rotated.</summary>
[Route("api/v1/client/webhooks")]
[HasPermission(Permissions.Webhooks.Manage)]
public sealed class WebhooksController : ApiControllerBase
{
    private readonly IWebhookService _webhooks;

    public WebhooksController(IWebhookService webhooks)
    {
        _webhooks = webhooks;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => ToActionResult(await _webhooks.ListAsync(cancellationToken));

    [HttpGet("events")]
    public IActionResult Events() => Ok(_webhooks.Events());

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) => ToActionResult(await _webhooks.GetAsync(id, cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create(CreateWebhookRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _webhooks.CreateAsync(request, cancellationToken), created => CreatedAtAction(nameof(Get), new { id = created.Endpoint.Id }, created));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateWebhookRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _webhooks.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) => ToActionResult(await _webhooks.DeleteAsync(id, cancellationToken));

    [HttpPost("{id:guid}/rotate-secret")]
    public async Task<IActionResult> RotateSecret(Guid id, CancellationToken cancellationToken) => ToActionResult(await _webhooks.RotateSecretAsync(id, cancellationToken));

    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> Test(Guid id, CancellationToken cancellationToken) => ToActionResult(await _webhooks.SendTestAsync(id, cancellationToken));

    [HttpGet("{id:guid}/deliveries")]
    public async Task<IActionResult> Deliveries(Guid id, [FromQuery] PageRequest page, CancellationToken cancellationToken) =>
        ToActionResult(await _webhooks.DeliveriesAsync(id, page, cancellationToken));

    [HttpPost("{id:guid}/deliveries/{deliveryId:long}/retry")]
    public async Task<IActionResult> Retry(Guid id, long deliveryId, CancellationToken cancellationToken) =>
        ToActionResult(await _webhooks.RetryAsync(id, deliveryId, cancellationToken));
}
