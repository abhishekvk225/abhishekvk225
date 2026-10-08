using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;

namespace NexaVerify.Web.Security;

/// <summary>
/// A sign-in that has passed the password step and waits for the second factor. The API's challenge token lives ONLY here, on the
/// server; the browser gets an opaque, HttpOnly cookie that names this record and nothing else.
/// </summary>
public sealed record MfaPending(string ChallengeToken, string Email, string? ClientIp, string? ReturnUrl, DateTimeOffset ExpiresAt)
{
    // Keep the challenge out of logs, exception messages and debugger strings.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Email = ").Append(Email);
        return true;
    }
}

public interface IMfaPendingStore
{
    /// <summary>Stores the pending sign-in and returns the opaque id for the cookie. It disappears when the challenge does.</summary>
    Task<string> CreateAsync(MfaPending pending, CancellationToken ct = default);

    Task<MfaPending?> GetAsync(string? id, CancellationToken ct = default);

    Task RemoveAsync(string? id, CancellationToken ct = default);
}

/// <summary>Encrypted with Data Protection and kept in the same distributed cache as the sessions (short TTL = the challenge lifetime).</summary>
public sealed class DistributedMfaPendingStore : IMfaPendingStore
{
    private const string Purpose = "NexaVerify.Web.MfaPending.v1";

    private readonly IDistributedCache _cache;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;

    public DistributedMfaPendingStore(IDistributedCache cache, IDataProtectionProvider protection, TimeProvider clock)
    {
        _cache = cache;
        _protector = protection.CreateProtector(Purpose);
        _clock = clock;
    }

    public async Task<string> CreateAsync(MfaPending pending, CancellationToken ct = default)
    {
        var id = DistributedSessionStore.NewId();
        var bytes = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(pending));
        await _cache.SetAsync(Key(id), bytes, new DistributedCacheEntryOptions { AbsoluteExpiration = pending.ExpiresAt }, ct);
        return id;
    }

    public async Task<MfaPending?> GetAsync(string? id, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128)
        {
            return null;
        }

        var bytes = await _cache.GetAsync(Key(id), ct);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            var pending = JsonSerializer.Deserialize<MfaPending>(_protector.Unprotect(bytes));
            if (pending is null || pending.ExpiresAt <= _clock.GetUtcNow())
            {
                await _cache.RemoveAsync(Key(id), ct);
                return null;
            }

            return pending;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            await _cache.RemoveAsync(Key(id), ct);
            return null;
        }
    }

    public Task RemoveAsync(string? id, CancellationToken ct = default) =>
        string.IsNullOrEmpty(id) ? Task.CompletedTask : _cache.RemoveAsync(Key(id), ct);

    private static string Key(string id) => "nv:mfa-pending:" + id;
}
