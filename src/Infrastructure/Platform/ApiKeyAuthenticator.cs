using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Api;
using NexaVerify.Application.Common;
using NexaVerify.Application.Tenancy;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Domain.Api;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Platform;

public sealed class ApiAuthOptions
{
    public const string SectionName = "ApiAuth";

    /// <summary>
    /// How long a node trusts its cached view of a key (status, scopes, the client's IP list and the client kill switch). Revocation,
    /// edits and the kill switch reach OTHER nodes within this time (the node that made the change is invalidated immediately).
    /// </summary>
    [Range(1, 60)]
    public int CacheSeconds { get; set; } = 5;

    /// <summary>Upper bound on remembered "unknown prefix" answers; beyond it entries are evicted, so probing cannot grow memory.</summary>
    [Range(100, 1_000_000)]
    public int NegativeCacheEntries { get; set; } = 5000;

    [Range(1, 60)]
    public int NegativeCacheSeconds { get; set; } = 5;
}

/// <summary>A size-limited cache for unknown key prefixes, separate from the positive cache so random probing can neither evict real keys nor grow without bound.</summary>
public sealed class ApiKeyNegativeCache
{
    private readonly MemoryCache _cache;
    private readonly TimeSpan _ttl;

    public ApiKeyNegativeCache(IOptions<ApiAuthOptions> options)
    {
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = options.Value.NegativeCacheEntries });
        _ttl = TimeSpan.FromSeconds(options.Value.NegativeCacheSeconds);
    }

    public bool Contains(string prefix) => _cache.TryGetValue(prefix, out _);

    public void Add(string prefix) => _cache.Set(prefix, true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = _ttl });

    public void Remove(string prefix) => _cache.Remove(prefix);
}

/// <summary>
/// Authenticates API keys. The lookup necessarily happens before the tenant is known, so it runs in platform scope and reads one
/// narrow projection by the key's public prefix; everything after that is bound to the key's own client. Failures are uniform:
/// an unknown prefix and a wrong secret produce the same answer in comparable time.
/// </summary>
public sealed class ApiKeyAuthenticator : IApiKeyAuthenticator
{
    /// <summary>A rule that never matches any address: a client allow-list that cannot be parsed denies everything instead of allowing everything.</summary>
    private const string CorruptRule = "!corrupt-allow-list";

    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);
    private static readonly byte[] DummyHash = SHA256.HashData("nexaverify-no-such-key"u8);

    private readonly AppDbContext _db;
    private readonly ITenantScope _scope;
    private readonly IMemoryCache _cache;
    private readonly IClientAccessGuard _clients;
    private readonly TimeProvider _time;
    private readonly ApiKeyNegativeCache _negative;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<ApiKeyAuthenticator> _logger;

    public ApiKeyAuthenticator(
        AppDbContext db,
        ITenantScope scope,
        IMemoryCache cache,
        IClientAccessGuard clients,
        TimeProvider time,
        ApiKeyNegativeCache negative,
        IOptions<ApiAuthOptions> options,
        ILogger<ApiKeyAuthenticator> logger)
    {
        _db = db;
        _scope = scope;
        _cache = cache;
        _clients = clients;
        _time = time;
        _negative = negative;
        _cacheTtl = TimeSpan.FromSeconds(options.Value.CacheSeconds);
        _logger = logger;
    }

    private sealed record Known(
        Guid Id, Guid ClientId, byte[] Hash, string Scopes, ApiKeyStatus Status, DateTime? ExpiresAt, int? RateLimit, string AllowedIps,
        string[] ClientAllowedIps, bool ClientApiDisabled);

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

        if (known.ClientApiDisabled)
        {
            return Error.Forbidden(ErrorCodes.ApiAccessDisabled, "API access for this account has been disabled. Please contact support.");
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

    public void Invalidate(string prefix)
    {
        _cache.Remove(CacheKey(prefix));
        _negative.Remove(prefix);
    }

    private async Task<Known?> LoadAsync(string prefix, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(CacheKey(prefix), out Known? cached))
        {
            return cached;
        }

        if (_negative.Contains(prefix))
        {
            return null;
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
                var clientSettings = await _db.ClientSettings.AsNoTracking()
                    .Where(s => s.ClientId == row.ClientId && (s.Key == SettingKeys.Integration.AllowedIps || s.Key == SettingKeys.Api.AccessDisabled))
                    .Select(s => new { s.Key, s.ValueJson })
                    .ToListAsync(CancellationToken.None);
                var clientRules = clientSettings.FirstOrDefault(s => s.Key == SettingKeys.Integration.AllowedIps)?.ValueJson;
                var disabled = clientSettings.FirstOrDefault(s => s.Key == SettingKeys.Api.AccessDisabled)?.ValueJson;
                loaded = new Known(
                    row.Id, row.ClientId, row.KeyHash, row.Scopes, row.Status, row.ExpiresAt, row.RateLimitPerMinute, row.AllowedIps,
                    ParseList(clientRules, row.ClientId), string.Equals(disabled?.Trim(), "true", StringComparison.OrdinalIgnoreCase));
            }
        }

        // Unknown prefixes are remembered too (briefly, in a bounded cache) so probing cannot turn into a database hammer.
        if (loaded is null)
        {
            _negative.Add(prefix);
        }
        else
        {
            _cache.Set(CacheKey(prefix), loaded, _cacheTtl);
        }

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

    /// <summary>A present but unparseable client allow-list DENIES (it must never silently become "no restriction").</summary>
    private string[] ParseList(string? json, Guid clientId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? throw new JsonException("The list is null.");
        }
        catch (JsonException)
        {
            _logger.LogError("The IP allow-list of client {ClientId} is corrupt; its API keys are refused until it is repaired", clientId);
            return [CorruptRule];
        }
    }

    private static string CacheKey(string prefix) => "apikey:" + prefix;
}
