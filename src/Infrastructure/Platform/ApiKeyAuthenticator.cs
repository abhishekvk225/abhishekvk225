using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Api;
using NexaVerify.Application.Common;
using NexaVerify.Application.Tenancy;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Domain.Api;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Platform;

/// <summary>
/// Authenticates API keys. The lookup necessarily happens before the tenant is known, so it runs in platform scope and reads one
/// narrow projection by the key's public prefix; everything after that is bound to the key's own client. Failures are uniform:
/// an unknown prefix and a wrong secret produce the same answer in comparable time.
/// </summary>
public sealed class ApiKeyAuthenticator : IApiKeyAuthenticator
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);
    private static readonly byte[] DummyHash = SHA256.HashData("nexaverify-no-such-key"u8);

    private readonly AppDbContext _db;
    private readonly ITenantScope _scope;
    private readonly IMemoryCache _cache;
    private readonly IClientAccessGuard _clients;
    private readonly TimeProvider _time;

    public ApiKeyAuthenticator(AppDbContext db, ITenantScope scope, IMemoryCache cache, IClientAccessGuard clients, TimeProvider time)
    {
        _db = db;
        _scope = scope;
        _cache = cache;
        _clients = clients;
        _time = time;
    }

    private sealed record Known(
        Guid Id, Guid ClientId, byte[] Hash, string Scopes, ApiKeyStatus Status, DateTime? ExpiresAt, int? RateLimit, string AllowedIps, string[] ClientAllowedIps);

    public async Task<Result<ApiKeyIdentity>> AuthenticateAsync(string rawKey, string? ipAddress, CancellationToken cancellationToken)
    {
        var invalid = Error.Unauthenticated(ErrorCodes.ApiKeyInvalid, "The API key is not valid.");
        if (ApiKeyMaterial.ParsePrefix(rawKey) is not { } prefix)
        {
            return invalid;
        }

        var known = await LoadAsync(prefix, cancellationToken);
        var matches = CryptographicOperations.FixedTimeEquals(ApiKeyMaterial.Hash(rawKey), known?.Hash ?? DummyHash);
        if (known is null || !matches)
        {
            return invalid;
        }

        // The secret is proven from here on, so specific reasons are safe to give.
        var now = _time.GetUtcNow().UtcDateTime;
        if (known.Status == ApiKeyStatus.Revoked)
        {
            return Error.Unauthenticated(ErrorCodes.ApiKeyRevoked, "This API key has been revoked.");
        }

        if (known.ExpiresAt is { } end && end <= now)
        {
            return Error.Unauthenticated(ErrorCodes.ApiKeyExpired, "This API key has expired.");
        }

        var keyRules = known.AllowedIps.Length == 0 ? [] : known.AllowedIps.Split(',');
        if (!IpRules.Allows(keyRules, ipAddress) || !IpRules.Allows(known.ClientAllowedIps, ipAddress))
        {
            return Error.Forbidden(ErrorCodes.IpNotAllowed, "This API key cannot be used from your IP address.");
        }

        if (await _clients.CheckAsync(known.ClientId, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        await TouchAsync(known.Id, ipAddress, now, cancellationToken);
        return new ApiKeyIdentity(known.ClientId, known.Id, known.Scopes.Length == 0 ? [] : known.Scopes.Split(','), known.RateLimit);
    }

    public void Invalidate(string prefix) => _cache.Remove(CacheKey(prefix));

    private async Task<Known?> LoadAsync(string prefix, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(CacheKey(prefix), out Known? cached))
        {
            return cached;
        }

        Known? loaded;
        using (_scope.BeginPlatform("api key authentication"))
        {
            var row = await _db.ApiKeys.AsNoTracking()
                .Where(k => k.KeyPrefix == prefix)
                .Select(k => new { k.Id, k.ClientId, k.KeyHash, k.Scopes, k.Status, k.ExpiresAt, k.RateLimitPerMinute, k.AllowedIps })
                .FirstOrDefaultAsync(CancellationToken.None);
            if (row is null)
            {
                loaded = null;
            }
            else
            {
                var clientRules = await _db.ClientSettings.AsNoTracking()
                    .Where(s => s.ClientId == row.ClientId && s.Key == SettingKeys.Integration.AllowedIps)
                    .Select(s => s.ValueJson)
                    .FirstOrDefaultAsync(CancellationToken.None);
                loaded = new Known(row.Id, row.ClientId, row.KeyHash, row.Scopes, row.Status, row.ExpiresAt, row.RateLimitPerMinute, row.AllowedIps, ParseList(clientRules));
            }
        }

        // Unknown prefixes are cached too (briefly) so probing cannot turn into a database hammer.
        _cache.Set(CacheKey(prefix), loaded, CacheTtl);
        return loaded;
    }

    /// <summary>Records "last used" at most once a minute per key; it is informational and must not add a write to every request.</summary>
    private async Task TouchAsync(Guid keyId, string? ip, DateTime now, CancellationToken cancellationToken)
    {
        var touchKey = "apikey-touch:" + keyId.ToString("N");
        if (_cache.TryGetValue(touchKey, out _))
        {
            return;
        }

        // Informational only: a failed write must never fail an otherwise valid request, and is retried on the next call.
        try
        {
            using (_scope.BeginPlatform("api key last-used"))
            {
                await _db.ApiKeys.Where(k => k.Id == keyId)
                    .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now).SetProperty(k => k.LastUsedIp, ip != null && ip.Length <= 45 ? ip : null), cancellationToken);
            }

            _cache.Set(touchKey, true, TouchInterval);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static string[] ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string CacheKey(string prefix) => "apikey:" + prefix;
}
