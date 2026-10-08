using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Domain.Api;
using NexaVerify.Domain.Common;
using NexaVerify.Application.Tenancy;

namespace NexaVerify.Application.Api;

public interface IApiKeyService
{
    Task<Result<IReadOnlyList<ApiKeyDto>>> ListAsync(CancellationToken cancellationToken);

    Task<Result<ApiKeyDto>> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<CreatedApiKeyDto>> CreateAsync(CreateApiKeyRequest request, CancellationToken cancellationToken);

    Task<Result<ApiKeyDto>> UpdateAsync(Guid id, UpdateApiKeyRequest request, CancellationToken cancellationToken);

    Task<Result<ApiKeyDto>> RevokeAsync(Guid id, RevokeApiKeyRequest request, CancellationToken cancellationToken);

    Task<Result<CreatedApiKeyDto>> RegenerateAsync(Guid id, RegenerateApiKeyRequest request, CancellationToken cancellationToken);

    IReadOnlyList<ApiScopeDto> Scopes();
}

public sealed class ApiKeyService : IApiKeyService
{
    private readonly IApiKeyRepository _keys;
    private readonly ICurrentUser _currentUser;
    private readonly IPermissionResolver _permissions;
    private readonly IClientSettingsService _settings;
    private readonly IApiKeyAuthenticator _authenticator;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITransactionLock _lock;
    private readonly TimeProvider _time;

    public ApiKeyService(
        IApiKeyRepository keys, ICurrentUser currentUser, IPermissionResolver permissions, IClientSettingsService settings,
        IApiKeyAuthenticator authenticator, IAuditService audit, IUnitOfWork unitOfWork, ITransactionLock transactionLock, TimeProvider time)
    {
        _lock = transactionLock;
        _keys = keys;
        _currentUser = currentUser;
        _permissions = permissions;
        _settings = settings;
        _authenticator = authenticator;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _time = time;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public IReadOnlyList<ApiScopeDto> Scopes() => ApiKeyScopes.Assignable.Select(s => new ApiScopeDto(s, ApiKeyScopes.Describe(s))).ToList();

    public async Task<Result<IReadOnlyList<ApiKeyDto>>> ListAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        return (await _keys.ListAsync(cancellationToken)).Select(k => ToDto(k, now)).ToList();
    }

    public async Task<Result<ApiKeyDto>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await _keys.GetAsync(id, cancellationToken) is { } key ? ToDto(key, Now) : Error.NotFound();

    public async Task<Result<CreatedApiKeyDto>> CreateAsync(CreateApiKeyRequest request, CancellationToken cancellationToken)
    {
        if (_currentUser.ClientId is not { } clientId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "API keys belong to client accounts.");
        }

        if (await CheckScopesAsync(request.Scopes, cancellationToken) is { } scopeError)
        {
            return scopeError;
        }

        var now = Now;
        var settings = await _settings.GetEffectiveAsync(clientId, cancellationToken);
        var (raw, prefix, hash) = ApiKeyMaterial.Generate();
        ApiKey key;
        try
        {
            key = ApiKey.Create(clientId, request.Name, prefix, hash, request.Scopes, Utc(request.ExpiresAt), request.RateLimitPerMinute, request.AllowedIps ?? [], now);
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        try
        {
            // The cap is checked and the key inserted under one transaction-scoped lock, so parallel creates cannot overshoot it.
            await _unitOfWork.ExecuteInTransactionAsync(
                async ct =>
                {
                    await _lock.AcquireAsync(CapLockName(clientId), ct);
                    if (await _keys.CountActiveAsync(now, ct) >= settings.Int(SettingKeys.Limits.MaxApiKeys))
                    {
                        throw new CapExceededException("APIKEY_LIMIT_REACHED", "The maximum number of active API keys for this account has been reached. Revoke one first.");
                    }

                    _keys.Add(key);
                    _audit.Record(new AuditEntry("apikey.created", nameof(ApiKey), key.Id.ToString(), clientId, NewValues: new { key.Name, key.KeyPrefix, key.Scopes, key.ExpiresAt }));
                    await _unitOfWork.SaveChangesAsync(ct);
                    return true;
                },
                cancellationToken);
        }
        catch (CapExceededException ex)
        {
            _unitOfWork.ClearTracked();
            return Error.Conflict(ex.Code, ex.Message);
        }

        return new CreatedApiKeyDto(ToDto(key, now), raw);
    }

    public async Task<Result<ApiKeyDto>> UpdateAsync(Guid id, UpdateApiKeyRequest request, CancellationToken cancellationToken)
    {
        var key = await _keys.GetAsync(id, cancellationToken);
        if (key is null)
        {
            return Error.NotFound();
        }

        if (!TryVersion(request.RowVersion, out var version))
        {
            return Error.Validation("rowVersion is not valid.", new Dictionary<string, string[]> { ["rowVersion"] = ["Invalid concurrency token."] });
        }

        if (await CheckScopesAsync(request.Scopes, cancellationToken) is { } scopeError)
        {
            return scopeError;
        }

        _keys.SetExpectedVersion(key, version);
        try
        {
            key.Update(request.Name, request.Scopes, Utc(request.ExpiresAt), request.RateLimitPerMinute, request.AllowedIps ?? [], Now);
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        _audit.Record(new AuditEntry("apikey.updated", nameof(ApiKey), key.Id.ToString(), key.ClientId, NewValues: new { key.Name, key.Scopes, key.ExpiresAt, key.RateLimitPerMinute }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _authenticator.Invalidate(key.KeyPrefix);
        return ToDto(key, Now);
    }

    public async Task<Result<ApiKeyDto>> RevokeAsync(Guid id, RevokeApiKeyRequest request, CancellationToken cancellationToken)
    {
        var key = await _keys.GetAsync(id, cancellationToken);
        if (key is null)
        {
            return Error.NotFound();
        }

        try
        {
            key.Revoke(request.Reason, _currentUser.ActorId, Now);
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        _audit.Record(new AuditEntry("apikey.revoked", nameof(ApiKey), key.Id.ToString(), key.ClientId, NewValues: new { key.KeyPrefix, request.Reason }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _authenticator.Invalidate(key.KeyPrefix);
        return ToDto(key, Now);
    }

    public async Task<Result<CreatedApiKeyDto>> RegenerateAsync(Guid id, RegenerateApiKeyRequest request, CancellationToken cancellationToken)
    {
        var old = await _keys.GetAsync(id, cancellationToken);
        if (old is null)
        {
            return Error.NotFound();
        }

        var now = Now;
        if (!old.IsUsable(now))
        {
            return Error.Conflict("APIKEY_NOT_USABLE", "Only an active key can be regenerated.");
        }

        // The replacement carries the old scopes, so the caller must hold them too (a key never out-ranks the person minting it).
        if (await CheckScopesAsync(old.ScopeList, cancellationToken) is { } scopeError)
        {
            return scopeError;
        }

        var (raw, prefix, hash) = ApiKeyMaterial.Generate();
        ApiKey replacement;
        try
        {
            replacement = ApiKey.Create(old.ClientId, old.Name, prefix, hash, old.ScopeList, old.ExpiresAt, old.RateLimitPerMinute, old.AllowedIpList, now, rotatedFrom: old.Id);
            if (request.GraceMinutes <= 0)
            {
                old.Revoke("Replaced by a regenerated key", _currentUser.ActorId, now);
            }
            else
            {
                old.ExpireAt(now.AddMinutes(request.GraceMinutes));
            }
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        // With a grace period both keys stay live, so the regeneration adds one to the active count (checked under the cap lock).
        var maxKeys = request.GraceMinutes > 0 ? (await _settings.GetEffectiveAsync(old.ClientId, cancellationToken)).Int(SettingKeys.Limits.MaxApiKeys) : int.MaxValue;
        try
        {
            await _unitOfWork.ExecuteInTransactionAsync(
                async ct =>
                {
                    if (request.GraceMinutes > 0)
                    {
                        await _lock.AcquireAsync(CapLockName(old.ClientId), ct);
                        if (await _keys.CountActiveAsync(now, ct) >= maxKeys)
                        {
                            throw new CapExceededException("APIKEY_LIMIT_REACHED", "The maximum number of active API keys for this account has been reached. Regenerate without a grace period or revoke one first.");
                        }
                    }

                    _keys.Add(replacement);
                    _audit.Record(new AuditEntry("apikey.regenerated", nameof(ApiKey), old.Id.ToString(), old.ClientId,
                        NewValues: new { OldPrefix = old.KeyPrefix, NewPrefix = prefix, request.GraceMinutes }));
                    await _unitOfWork.SaveChangesAsync(ct);
                    return true;
                },
                cancellationToken);
        }
        catch (CapExceededException ex)
        {
            _unitOfWork.ClearTracked();
            return Error.Conflict(ex.Code, ex.Message);
        }

        _authenticator.Invalidate(old.KeyPrefix);
        return new CreatedApiKeyDto(ToDto(replacement, now), raw);
    }

    private static string CapLockName(Guid clientId) => "apikeys:" + clientId.ToString("N");

    /// <summary>Scopes must be integration scopes AND ones the caller holds themselves (a key can never out-rank its creator).</summary>
    private async Task<Error?> CheckScopesAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken)
    {
        var invalid = scopes.Where(s => !ApiKeyScopes.Assignable.Contains(s)).ToList();
        if (invalid.Count > 0)
        {
            return Error.Validation("Some scopes cannot be given to an API key.",
                new Dictionary<string, string[]> { ["scopes"] = invalid.Select(s => $"'{s}' is not an assignable scope.").ToArray() });
        }

        var held = await _permissions.GetPermissionsAsync(_currentUser.Roles, cancellationToken);
        return scopes.Any(s => !held.Contains(s))
            ? Error.Forbidden(ErrorCodes.Forbidden, "You cannot give an API key permissions you do not have yourself.")
            : null;
    }

    private static DateTime? Utc(DateTime? value) => value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

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

    internal static ApiKeyDto ToDto(ApiKey k, DateTime now) => new(
        k.Id, k.Name, k.KeyPrefix, k.ScopeList, k.Status.ToString(),
        k.Status == ApiKeyStatus.Revoked ? "Revoked" : k.IsExpired(now) ? "Expired" : "Active",
        k.ExpiresAt, k.LastUsedAt, k.LastUsedIp, k.RateLimitPerMinute, k.AllowedIpList, k.RotatedFromKeyId, k.CreatedAt, k.RevokedAt, k.RevokedReason,
        Convert.ToBase64String(k.RowVersion));
}

public interface IApiLogService
{
    Task<Result<PagedResult<ApiRequestLogDto>>> ListAsync(ApiLogQuery query, CancellationToken cancellationToken);
}

public sealed class ApiLogService : IApiLogService
{
    private readonly IApiLogRepository _logs;

    public ApiLogService(IApiLogRepository logs)
    {
        _logs = logs;
    }

    public async Task<Result<PagedResult<ApiRequestLogDto>>> ListAsync(ApiLogQuery query, CancellationToken cancellationToken)
    {
        var paging = new PageRequest { Page = query.Page, PageSize = query.PageSize }.Normalize();
        var (items, total) = await _logs.ListAsync(query.ApiKeyId, query.StatusClass, Utc(query.From), Utc(query.To), paging.Skip, paging.PageSize, cancellationToken);
        return new PagedResult<ApiRequestLogDto>(
            items.Select(l => new ApiRequestLogDto(l.Id, l.ApiKeyId, l.Method, l.RouteTemplate, l.StatusCode, l.DurationMs, l.IpAddress, l.ErrorCode, l.CorrelationId, l.CreatedAt)).ToList(),
            paging.Page, paging.PageSize, total);
    }

    private static DateTime? Utc(DateTime? value) => value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
}
