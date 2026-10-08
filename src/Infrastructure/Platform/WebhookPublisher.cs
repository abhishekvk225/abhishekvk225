using NexaVerify.Application.Api;
using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Api;

namespace NexaVerify.Infrastructure.Platform;

/// <summary>
/// Stages one delivery row per subscribed endpoint in the caller's unit of work. The endpoint list is read fresh (one indexed query
/// on a tiny table) rather than cached: a cached id of an endpoint deleted meanwhile would make the INSERT fail and take the caller's
/// business transaction down with it.
/// </summary>
public sealed class WebhookPublisher : IWebhookPublisher
{
    private readonly IWebhookRepository _webhooks;
    private readonly TimeProvider _time;

    public WebhookPublisher(IWebhookRepository webhooks, TimeProvider time)
    {
        _webhooks = webhooks;
        _time = time;
    }

    public async Task PublishAsync(Guid clientId, string eventType, object data, CancellationToken cancellationToken)
    {
        var targets = await _webhooks.ListActiveForEventAsync(eventType, cancellationToken);
        if (targets.Count == 0)
        {
            return;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var eventId = Guid.CreateVersion7();
        var payload = WebhookSigning.Envelope(eventId, eventType, now, data);
        foreach (var endpoint in targets)
        {
            _webhooks.Add(WebhookDelivery.Queue(clientId, endpoint.Id, eventId, eventType, payload, now));
        }
    }
}
