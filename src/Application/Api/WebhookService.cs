using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentValidation;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Persistence;
using NexaVerify.Application.Tenancy;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Domain.Api;
using NexaVerify.Domain.Common;

namespace NexaVerify.Application.Api;

/// <summary>Decides whether a client-supplied URL may be called by the server (SSRF protection). Re-checked, on the resolved addresses, at send time.</summary>
public interface IWebhookUrlGuard
{
    /// <summary>Returns null when the URL is acceptable, otherwise a message for the client.</summary>
    Task<string?> ValidateAsync(string url, CancellationToken cancellationToken);
}

/// <summary>Stages events for delivery in the CURRENT unit of work (transactional outbox): committed or rolled back with the business change.</summary>
public interface IWebhookPublisher
{
    Task PublishAsync(Guid clientId, string eventType, object data, CancellationToken cancellationToken);
}

public static class WebhookSigning
{
    public const string SecretPrefix = "whsec_";

    public static string NewSecret() => SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary><c>t=&lt;unix&gt;,v1=&lt;hex HMAC-SHA256(secret, t + "." + body)&gt;</c> — the timestamp lets receivers reject replays.</summary>
    public static string Sign(string secret, long unixSeconds, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(unixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + body));
        return $"t={unixSeconds},v1={Convert.ToHexString(mac).ToLowerInvariant()}";
    }

    public static string Envelope(Guid eventId, string eventType, DateTime createdAt, object data) =>
        JsonSerializer.Serialize(new { id = eventId, type = eventType, createdAt, data }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

public interface IWebhookService
{
    IReadOnlyList<WebhookEventDto> Events();

    Task<Result<IReadOnlyList<WebhookEndpointDto>>> ListAsync(CancellationToken cancellationToken);

    Task<Result<WebhookEndpointDto>> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<CreatedWebhookDto>> CreateAsync(CreateWebhookRequest request, CancellationToken cancellationToken);

    Task<Result<WebhookEndpointDto>> UpdateAsync(Guid id, UpdateWebhookRequest request, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<CreatedWebhookDto>> RotateSecretAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<WebhookDeliveryDto>> SendTestAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<PagedResult<WebhookDeliveryDto>>> DeliveriesAsync(Guid id, PageRequest page, CancellationToken cancellationToken);

    Task<Result<WebhookDeliveryDto>> RetryAsync(Guid endpointId, long deliveryId, CancellationToken cancellationToken);
}

public sealed class WebhookService : IWebhookService
{
    private const string SecretPurpose = "webhook-secret";

    private readonly IWebhookRepository _webhooks;
    private readonly IWebhookUrlGuard _guard;
    private readonly IClientEncryption _encryption;
    private readonly IClientSettingsService _settings;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;

    public WebhookService(
        IWebhookRepository webhooks, IWebhookUrlGuard guard, IClientEncryption encryption, IClientSettingsService settings,
        ICurrentUser currentUser, IAuditService audit, IUnitOfWork unitOfWork, TimeProvider time)
    {
        _webhooks = webhooks;
        _guard = guard;
        _encryption = encryption;
        _settings = settings;
        _currentUser = currentUser;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _time = time;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public IReadOnlyList<WebhookEventDto> Events() =>
    [
        new(WebhookEvents.RecognitionCompleted, "A verification, identification or enrolment finished."),
        new(WebhookEvents.LicenseLowBalance, "Remaining credits fell below your alert threshold."),
        new(WebhookEvents.LicenseExpiring, "A license is about to expire."),
        new(WebhookEvents.LicenseExpired, "A license expired."),
        new(WebhookEvents.LicenseExhausted, "All credits were used."),
        new(WebhookEvents.ApiKeyExpiring, "An API key is about to expire."),
    ];

    public async Task<Result<IReadOnlyList<WebhookEndpointDto>>> ListAsync(CancellationToken cancellationToken) =>
        (await _webhooks.ListEndpointsAsync(cancellationToken)).Select(ToDto).ToList();

    public async Task<Result<WebhookEndpointDto>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await _webhooks.GetEndpointAsync(id, cancellationToken) is { } endpoint ? ToDto(endpoint) : Error.NotFound();

    public async Task<Result<CreatedWebhookDto>> CreateAsync(CreateWebhookRequest request, CancellationToken cancellationToken)
    {
        if (_currentUser.ClientId is not { } clientId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Webhooks belong to client accounts.");
        }

        if (!(await _settings.GetEffectiveAsync(clientId, cancellationToken)).Bool(SettingKeys.Integration.WebhooksEnabled))
        {
            return Error.Forbidden("WEBHOOKS_DISABLED", "Webhooks are not enabled for this account.");
        }

        if (await _webhooks.CountEndpointsAsync(cancellationToken) >= WebhookEndpoint.MaxPerClient)
        {
            return Error.Conflict("WEBHOOK_LIMIT_REACHED", $"At most {WebhookEndpoint.MaxPerClient} webhook endpoints are allowed.");
        }

        if (await _guard.ValidateAsync(request.Url, cancellationToken) is { } problem)
        {
            return UrlError(problem);
        }

        var secret = WebhookSigning.NewSecret();
        WebhookEndpoint endpoint;
        try
        {
            endpoint = WebhookEndpoint.Create(clientId, request.Name, request.Url, request.Events, await _encryption.EncryptAsync(clientId, Encoding.UTF8.GetBytes(secret), SecretPurpose, cancellationToken));
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        _webhooks.Add(endpoint);
        _audit.Record(new AuditEntry("webhook.created", nameof(WebhookEndpoint), endpoint.Id.ToString(), clientId, NewValues: new { endpoint.Name, endpoint.Url, endpoint.Events }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return new CreatedWebhookDto(ToDto(endpoint), secret);
    }

    public async Task<Result<WebhookEndpointDto>> UpdateAsync(Guid id, UpdateWebhookRequest request, CancellationToken cancellationToken)
    {
        var endpoint = await _webhooks.GetEndpointAsync(id, cancellationToken);
        if (endpoint is null)
        {
            return Error.NotFound();
        }

        if (!TryVersion(request.RowVersion, out var version))
        {
            return Error.Validation("rowVersion is not valid.", new Dictionary<string, string[]> { ["rowVersion"] = ["Invalid concurrency token."] });
        }

        if (!string.Equals(endpoint.Url, request.Url.Trim(), StringComparison.Ordinal) && await _guard.ValidateAsync(request.Url, cancellationToken) is { } problem)
        {
            return UrlError(problem);
        }

        _webhooks.SetExpectedVersion(endpoint, version);
        try
        {
            endpoint.Update(request.Name, request.Url, request.Events);
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        if (request.Enabled && endpoint.Status == WebhookStatus.Disabled)
        {
            endpoint.Enable();
        }
        else if (!request.Enabled && endpoint.Status == WebhookStatus.Active)
        {
            endpoint.Disable("Disabled by user", Now);
        }

        _audit.Record(new AuditEntry("webhook.updated", nameof(WebhookEndpoint), endpoint.Id.ToString(), endpoint.ClientId, NewValues: new { endpoint.Name, endpoint.Url, endpoint.Events, Status = endpoint.Status.ToString() }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(endpoint);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var endpoint = await _webhooks.GetEndpointAsync(id, cancellationToken);
        if (endpoint is null)
        {
            return Error.NotFound();
        }

        _webhooks.Remove(endpoint);
        _audit.Record(new AuditEntry("webhook.deleted", nameof(WebhookEndpoint), endpoint.Id.ToString(), endpoint.ClientId, OldValues: new { endpoint.Name, endpoint.Url }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<CreatedWebhookDto>> RotateSecretAsync(Guid id, CancellationToken cancellationToken)
    {
        var endpoint = await _webhooks.GetEndpointAsync(id, cancellationToken);
        if (endpoint is null)
        {
            return Error.NotFound();
        }

        var secret = WebhookSigning.NewSecret();
        endpoint.ReplaceSecret(await _encryption.EncryptAsync(endpoint.ClientId, Encoding.UTF8.GetBytes(secret), SecretPurpose, cancellationToken));
        _audit.Record(new AuditEntry("webhook.secret_rotated", nameof(WebhookEndpoint), endpoint.Id.ToString(), endpoint.ClientId));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return new CreatedWebhookDto(ToDto(endpoint), secret);
    }

    public async Task<Result<WebhookDeliveryDto>> SendTestAsync(Guid id, CancellationToken cancellationToken)
    {
        var endpoint = await _webhooks.GetEndpointAsync(id, cancellationToken);
        if (endpoint is null)
        {
            return Error.NotFound();
        }

        if (endpoint.Status != WebhookStatus.Active)
        {
            return Error.Conflict("WEBHOOK_DISABLED", "Enable the endpoint before sending a test event.");
        }

        var now = Now;
        var eventId = Guid.CreateVersion7();
        var delivery = WebhookDelivery.Queue(endpoint.ClientId, endpoint.Id, eventId, WebhookEvents.Test,
            WebhookSigning.Envelope(eventId, WebhookEvents.Test, now, new { message = "This is a test event from NexaVerify." }), now);
        _webhooks.Add(delivery);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(delivery);
    }

    public async Task<Result<PagedResult<WebhookDeliveryDto>>> DeliveriesAsync(Guid id, PageRequest page, CancellationToken cancellationToken)
    {
        if (await _webhooks.GetEndpointAsync(id, cancellationToken) is null)
        {
            return Error.NotFound();
        }

        var paging = page.Normalize();
        var (items, total) = await _webhooks.ListDeliveriesAsync(id, paging.Skip, paging.PageSize, cancellationToken);
        return new PagedResult<WebhookDeliveryDto>(items.Select(ToDto).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<Result<WebhookDeliveryDto>> RetryAsync(Guid endpointId, long deliveryId, CancellationToken cancellationToken)
    {
        var delivery = await _webhooks.GetDeliveryAsync(deliveryId, cancellationToken);
        if (delivery is null || delivery.EndpointId != endpointId)
        {
            return Error.NotFound();
        }

        if (delivery.Status != DeliveryStatus.Abandoned)
        {
            return Error.Conflict("DELIVERY_NOT_ABANDONED", "Only a delivery that gave up can be retried.");
        }

        delivery.Requeue(Now);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(delivery);
    }

    private static Error UrlError(string problem) =>
        Error.Validation("The webhook URL is not acceptable.", new Dictionary<string, string[]> { ["url"] = [problem] });

    private static bool TryVersion(string value, out byte[] version)
    {
        try
        {
            version = Convert.FromBase64String(value);
            return version.Length > 0;
        }
        catch (FormatException)
        {
            version = [];
            return false;
        }
    }

    internal static WebhookEndpointDto ToDto(WebhookEndpoint e) => new(
        e.Id, e.Name, e.Url, e.EventList, e.Status.ToString(), e.FailureCount, e.DisabledAt, e.DisabledReason, e.CreatedAt, Convert.ToBase64String(e.RowVersion));

    internal static WebhookDeliveryDto ToDto(WebhookDelivery d) => new(
        d.Id, d.EventId, d.EventType, d.Status.ToString(), d.Attempts, d.NextAttemptAt, d.LastStatusCode, d.LastError, d.CreatedAt, d.DeliveredAt);
}

public sealed class CreateWebhookRequestValidator : AbstractValidator<CreateWebhookRequest>
{
    public CreateWebhookRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Url).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Events).NotEmpty().Must(e => e.Count <= 20);
    }
}

public sealed class UpdateWebhookRequestValidator : AbstractValidator<UpdateWebhookRequest>
{
    public UpdateWebhookRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Url).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Events).NotEmpty().Must(e => e.Count <= 20);
        RuleFor(x => x.RowVersion).NotEmpty().MaximumLength(64);
    }
}
