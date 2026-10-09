using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NexaVerify.Api.Http;
using NexaVerify.Application.Billing;
using NexaVerify.Application.Common;

namespace NexaVerify.Api.Controllers.Billing;

/// <summary>
/// Where payment providers report what happened. Anonymous by nature: the only credential is the provider's signature over the RAW body,
/// which is checked before anything is parsed or any database work is done. A bad signature is a 400. The answer is a quick 200 once the
/// event is recorded; redelivery is harmless (the event ledger and the order's conditional UPDATE make processing idempotent).
/// </summary>
[AllowAnonymous]
[Route("api/v1/billing/webhooks")]
public sealed class BillingWebhooksController : ApiControllerBase
{
    private readonly IBillingWebhookService _webhooks;
    private readonly BillingOptions _options;

    public BillingWebhooksController(IBillingWebhookService webhooks, IOptions<BillingOptions> options)
    {
        _webhooks = webhooks;
        _options = options.Value;
    }

    [HttpPost("{provider}")]
    [Consumes("application/json", "application/*+json", "text/plain")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Receive(string provider, CancellationToken cancellationToken)
    {
        var max = _options.WebhookMaxBodyBytes;
        if (HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = max;
        }

        if (Request.ContentLength > max)
        {
            return ProblemFor(Error.PayloadTooLarge("The webhook body is too large."));
        }

        byte[] body;
        using (var buffer = new MemoryStream())
        {
            var chunk = new byte[8192];
            int read;
            while ((read = await Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > max)
                {
                    return ProblemFor(Error.PayloadTooLarge("The webhook body is too large."));
                }

                buffer.Write(chunk, 0, read);
            }

            body = buffer.ToArray();
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in WebhookHeaders.Names)
        {
            if (Request.Headers.TryGetValue(name, out var values) && values.Count == 1 && values[0] is { Length: > 0 and <= 2048 } value)
            {
                headers[name] = value;
            }
        }

        return ToActionResult(await _webhooks.HandleAsync(provider, headers, body, cancellationToken), () => Ok(new { received = true }));
    }
}
