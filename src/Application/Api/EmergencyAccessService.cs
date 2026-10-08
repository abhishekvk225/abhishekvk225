using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Domain.Api;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Application.Api;

public interface IEmergencyAccessService
{
    /// <summary>Revokes every active API key of the client. Also reports whether the client-wide kill switch is on.</summary>
    Task<Result<EmergencyRevokeResultDto>> RevokeAllKeysAsync(Guid clientId, EmergencyRevokeRequest request, CancellationToken cancellationToken);

    Task<Result<ApiKeyDto>> RevokeKeyAsync(Guid clientId, Guid keyId, EmergencyRevokeRequest request, CancellationToken cancellationToken);

    Task<Result<ApiAccessDto>> GetApiAccessAsync(Guid clientId, CancellationToken cancellationToken);

    /// <summary>Switches the client-wide kill switch (<c>api.accessDisabled</c>) on or off. Keys are not touched, so switching it off restores access.</summary>
    Task<Result<ApiAccessDto>> SetApiAccessAsync(Guid clientId, SetApiAccessRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Break-glass controls for platform staff when a client's keys leak or an integration misbehaves. Every action needs a reason and is
/// audited against the client. Revocation applies on this node at once and on other nodes within the API-key cache TTL
/// (<c>ApiAuth:CacheSeconds</c>, default 5 s); the kill switch uses the same path.
/// </summary>
public sealed class EmergencyAccessService : IEmergencyAccessService
{
    private readonly IApiKeyRepository _keys;
    private readonly IClientRepository _clients;
    private readonly IClientSettingRepository _settings;
    private readonly IApiKeyAuthenticator _authenticator;
    private readonly IAuditService _audit;
    private readonly ICurrentUser _currentUser;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;

    public EmergencyAccessService(
        IApiKeyRepository keys, IClientRepository clients, IClientSettingRepository settings, IApiKeyAuthenticator authenticator,
        IAuditService audit, ICurrentUser currentUser, IUnitOfWork unitOfWork, TimeProvider time)
    {
        _keys = keys;
        _clients = clients;
        _settings = settings;
        _authenticator = authenticator;
        _audit = audit;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
        _time = time;
    }

    public async Task<Result<EmergencyRevokeResultDto>> RevokeAllKeysAsync(Guid clientId, EmergencyRevokeRequest request, CancellationToken cancellationToken)
    {
        if (await RealClientAsync(clientId, cancellationToken) is null)
        {
            return Error.NotFound("The client was not found.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var active = await _keys.ListActiveForClientAsync(clientId, cancellationToken);
        var reason = "Emergency revocation: " + request.Reason.Trim();
        foreach (var key in active)
        {
            key.Revoke(reason.Length > 500 ? reason[..500] : reason, _currentUser.ActorId, now);
        }

        _audit.Record(new AuditEntry("apikey.emergency_revoked_all", nameof(ApiKey), clientId.ToString(), clientId,
            NewValues: new { Count = active.Count, Reason = request.Reason.Trim(), Prefixes = active.Select(k => k.KeyPrefix).ToList() }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        foreach (var key in active)
        {
            _authenticator.Invalidate(key.KeyPrefix);
        }

        return new EmergencyRevokeResultDto(active.Count, await IsDisabledAsync(clientId, cancellationToken));
    }

    public async Task<Result<ApiKeyDto>> RevokeKeyAsync(Guid clientId, Guid keyId, EmergencyRevokeRequest request, CancellationToken cancellationToken)
    {
        var key = await _keys.GetForClientAsync(clientId, keyId, cancellationToken);
        if (key is null)
        {
            return Error.NotFound();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        try
        {
            var text = "Emergency revocation: " + request.Reason.Trim();
            key.Revoke(text.Length > 500 ? text[..500] : text, _currentUser.ActorId, now);
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        _audit.Record(new AuditEntry("apikey.emergency_revoked", nameof(ApiKey), key.Id.ToString(), clientId,
            NewValues: new { key.KeyPrefix, Reason = request.Reason.Trim() }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _authenticator.Invalidate(key.KeyPrefix);
        return ApiKeyService.ToDto(key, now);
    }

    public async Task<Result<ApiAccessDto>> GetApiAccessAsync(Guid clientId, CancellationToken cancellationToken) =>
        await RealClientAsync(clientId, cancellationToken) is null
            ? Error.NotFound("The client was not found.")
            : new ApiAccessDto(await IsDisabledAsync(clientId, cancellationToken));

    public async Task<Result<ApiAccessDto>> SetApiAccessAsync(Guid clientId, SetApiAccessRequest request, CancellationToken cancellationToken)
    {
        if (await RealClientAsync(clientId, cancellationToken) is null)
        {
            return Error.NotFound("The client was not found.");
        }

        var existing = (await _settings.GetAllAsync(clientId, cancellationToken)).FirstOrDefault(s => s.Key == SettingKeys.Api.AccessDisabled);
        var was = existing is not null && existing.ValueJson.Trim() == "true";
        if (request.Disabled)
        {
            if (existing is null)
            {
                _settings.Add(ClientSetting.Create(clientId, SettingKeys.Api.AccessDisabled, "true"));
            }
            else
            {
                existing.SetValue("true");
            }
        }
        else if (existing is not null)
        {
            _settings.Remove(existing); // back to the default (enabled): store nothing
        }

        _audit.Record(new AuditEntry(request.Disabled ? "client.api_access_disabled" : "client.api_access_enabled", nameof(ClientSetting), clientId.ToString(), clientId,
            OldValues: new { Disabled = was }, NewValues: new { request.Disabled, Reason = request.Reason.Trim() }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        foreach (var key in await _keys.ListActiveForClientAsync(clientId, cancellationToken))
        {
            _authenticator.Invalidate(key.KeyPrefix); // this node sees the switch immediately; other nodes within the cache TTL
        }

        return new ApiAccessDto(request.Disabled);
    }

    private async Task<Client?> RealClientAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var client = await _clients.GetByIdAsync(clientId, cancellationToken);
        return client is null || client.IsSystem ? null : client;
    }

    private async Task<bool> IsDisabledAsync(Guid clientId, CancellationToken cancellationToken) =>
        (await _settings.GetAllAsync(clientId, cancellationToken)).Any(s => s.Key == SettingKeys.Api.AccessDisabled && s.ValueJson.Trim() == "true");
}
