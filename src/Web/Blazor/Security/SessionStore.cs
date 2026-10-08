using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace NexaVerify.Web.Security;

/// <summary>
/// Server-side session storage. The browser only ever holds the opaque session id; tokens stay here.
/// Idle (sliding) and absolute expiry are enforced on every read.
/// </summary>
public interface ISessionStore
{
    /// <summary>Returns the live session, or null when it does not exist, has been idle too long or has passed its absolute lifetime.</summary>
    Task<PortalSession?> GetAsync(string id, CancellationToken ct = default);

    Task SaveAsync(PortalSession session, CancellationToken ct = default);

    /// <summary>
    /// Atomically (per session, per process) re-reads the session and applies <paramref name="mutate"/>. Returning null from the
    /// mutation leaves the session unchanged. Returns the stored session, or null when it no longer exists.
    /// </summary>
    Task<PortalSession?> UpdateAsync(string id, Func<PortalSession, PortalSession?> mutate, CancellationToken ct = default);

    /// <summary>Records activity (slides the idle window). Cheap: writes at most every few seconds.</summary>
    Task TouchAsync(string id, CancellationToken ct = default);

    Task RemoveAsync(string id, CancellationToken ct = default);
}

/// <summary>
/// <see cref="ISessionStore"/> over <see cref="IDistributedCache"/> (in-memory by default; plug Redis/SQL in for several nodes).
/// Payloads are encrypted with ASP.NET Data Protection so a cache dump does not reveal tokens.
/// </summary>
public sealed class DistributedSessionStore : ISessionStore
{
    private const string Purpose = "NexaVerify.Web.SessionStore.v1";
    private static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(10);

    private readonly IDistributedCache _cache;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;
    private readonly SessionOptions _options;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public DistributedSessionStore(IDistributedCache cache, IDataProtectionProvider protection, TimeProvider clock, IOptions<SessionOptions> options)
    {
        _cache = cache;
        _protector = protection.CreateProtector(Purpose);
        _clock = clock;
        _options = options.Value;
    }

    /// <summary>A fresh unguessable session id (256 bits, URL-safe).</summary>
    public static string NewId() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task<PortalSession?> GetAsync(string id, CancellationToken ct = default)
    {
        var session = await ReadAsync(id, ct);
        if (session is null)
        {
            return null;
        }

        var now = _clock.GetUtcNow();
        if (now >= session.AbsoluteExpiresAt || now - session.LastSeenAt >= _options.IdleTimeout)
        {
            await RemoveAsync(id, ct);
            return null;
        }

        return session;
    }

    public async Task SaveAsync(PortalSession session, CancellationToken ct = default)
    {
        var gate = LockFor(session.Id);
        await gate.WaitAsync(ct);
        try
        {
            await WriteAsync(session, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PortalSession?> UpdateAsync(string id, Func<PortalSession, PortalSession?> mutate, CancellationToken ct = default)
    {
        var gate = LockFor(id);
        await gate.WaitAsync(ct);
        try
        {
            var current = await GetAsync(id, ct);
            if (current is null)
            {
                return null;
            }

            var next = mutate(current);
            if (next is null)
            {
                return current;
            }

            // The id and the absolute lifetime are fixed at creation; a mutation cannot extend them.
            next = next with { Id = current.Id, AbsoluteExpiresAt = current.AbsoluteExpiresAt, CreatedAt = current.CreatedAt };
            await WriteAsync(next, ct);
            return next;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task TouchAsync(string id, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var current = await GetAsync(id, ct);
        if (current is null || now - current.LastSeenAt < TouchInterval)
        {
            return;
        }

        await UpdateAsync(id, s => s with { LastSeenAt = now }, ct);
    }

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        await _cache.RemoveAsync(CacheKey(id), ct);
        _locks.TryRemove(id, out _);
    }

    private static string CacheKey(string id) => "nv:session:" + id;

    private SemaphoreSlim LockFor(string id) => _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    private async Task<PortalSession?> ReadAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128)
        {
            return null;
        }

        var bytes = await _cache.GetAsync(CacheKey(id), ct);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PortalSession>(_protector.Unprotect(bytes));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Unreadable (keys rotated, tampered): treat as no session.
            await _cache.RemoveAsync(CacheKey(id), ct);
            return null;
        }
    }

    private async Task WriteAsync(PortalSession session, CancellationToken ct)
    {
        var bytes = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(session));
        var idleEnds = session.LastSeenAt + _options.IdleTimeout + TimeSpan.FromMinutes(1);
        var expires = idleEnds < session.AbsoluteExpiresAt ? idleEnds : session.AbsoluteExpiresAt;
        await _cache.SetAsync(CacheKey(session.Id), bytes, new DistributedCacheEntryOptions { AbsoluteExpiration = expires }, ct);
    }
}
