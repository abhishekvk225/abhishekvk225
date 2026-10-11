using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Infrastructure.Security;

/// <summary>
/// Envelope encryption: each client has its own random 256-bit data key, stored only wrapped by the master key. Ciphertexts are
/// AES-256-GCM with the client id, purpose and key version as authenticated data, so a ciphertext cannot be moved to another
/// client or purpose. Payload layout: keyVersion(4, big-endian) | nonce(12) | tag(16) | ciphertext.
/// </summary>
public sealed class ClientKeyService : IClientKeyProvisioner, IClientEncryption
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int VersionSize = 4;
    private static readonly TimeSpan KeyCacheTtl = TimeSpan.FromMinutes(10);

    private readonly Persistence.AppDbContext _db;
    private readonly MasterKeyProvider _master;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _time;

    public ClientKeyService(Persistence.AppDbContext db, MasterKeyProvider master, IMemoryCache cache, TimeProvider time)
    {
        _db = db;
        _master = master;
        _cache = cache;
        _time = time;
    }

    public Task ProvisionAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var dataKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            _db.ClientKeys.Add(ClientKey.Create(clientId, 1, DataKeyWrapper.Wrap(_master, _master.CurrentKeyId, clientId, 1, dataKey), _master.CurrentKeyId, _time.GetUtcNow().UtcDateTime));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }

        return Task.CompletedTask;
    }

    public async Task<byte[]> EncryptAsync(Guid clientId, ReadOnlyMemory<byte> plaintext, string purpose, CancellationToken cancellationToken)
    {
        var key = await GetActiveKeyAsync(clientId, cancellationToken);
        var payload = new byte[VersionSize + NonceSize + TagSize + plaintext.Length];
        BinaryPrimitives.WriteInt32BigEndian(payload, key.Version);
        var nonce = payload.AsSpan(VersionSize, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key.Material, TagSize);
        aes.Encrypt(nonce, plaintext.Span, payload.AsSpan(VersionSize + NonceSize + TagSize), payload.AsSpan(VersionSize + NonceSize, TagSize), Aad(clientId, purpose, key.Version));
        return payload;
    }

    public async Task<byte[]> DecryptAsync(Guid clientId, ReadOnlyMemory<byte> payload, string purpose, CancellationToken cancellationToken)
    {
        if (payload.Length < VersionSize + NonceSize + TagSize)
        {
            throw new CryptographicException("Ciphertext is malformed.");
        }

        var version = BinaryPrimitives.ReadInt32BigEndian(payload.Span);
        var key = await GetKeyAsync(clientId, version, cancellationToken);
        var span = payload.Span; // taken after the last await (spans cannot live across awaits)
        var plaintext = new byte[payload.Length - VersionSize - NonceSize - TagSize];
        using var aes = new AesGcm(key.Material, TagSize);
        aes.Decrypt(span.Slice(VersionSize, NonceSize), span[(VersionSize + NonceSize + TagSize)..], span.Slice(VersionSize + NonceSize, TagSize), plaintext, Aad(clientId, purpose, version));
        return plaintext;
    }

    public async Task DestroyKeysAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var keys = await _db.ClientKeys.Where(k => k.ClientId == clientId).ToListAsync(cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var key in keys)
        {
            key.Destroy(now);
            _cache.Remove(CacheKey(clientId, key.KeyVersion));
        }

        _cache.Remove(ActiveKey(clientId));
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<DataKey> GetActiveKeyAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var version = await _cache.GetOrCreateAsync(ActiveKey(clientId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = KeyCacheTtl;
            return await _db.ClientKeys.AsNoTracking()
                .Where(k => k.ClientId == clientId && k.Status == ClientKeyStatus.Active)
                .OrderByDescending(k => k.KeyVersion)
                .Select(k => (int?)k.KeyVersion)
                .FirstOrDefaultAsync(cancellationToken);
        });

        return version is { } v
            ? await GetKeyAsync(clientId, v, cancellationToken)
            : throw new CryptographicException("The client has no active encryption key.");
    }

    private async Task<DataKey> GetKeyAsync(Guid clientId, int version, CancellationToken cancellationToken)
    {
        var key = await _cache.GetOrCreateAsync(CacheKey(clientId, version), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = KeyCacheTtl;
            var row = await _db.ClientKeys.AsNoTracking().FirstOrDefaultAsync(k => k.ClientId == clientId && k.KeyVersion == version, cancellationToken)
                ?? throw new CryptographicException("Encryption key not found.");
            if (row.Status == ClientKeyStatus.Destroyed || row.WrappedDataKey.Length == 0)
            {
                throw new CryptographicException("The client's encryption key has been destroyed.");
            }

            return new DataKey(version, DataKeyWrapper.Unwrap(_master, row.MasterKeyId, clientId, version, row.WrappedDataKey));
        });

        return key!;
    }

    private static byte[] Aad(Guid clientId, string purpose, int version) => Encoding.UTF8.GetBytes($"data|{clientId:N}|{purpose}|{version}");

    private static string CacheKey(Guid clientId, int version) => $"dek:{clientId:N}:{version}";

    private static string ActiveKey(Guid clientId) => $"dek-active:{clientId:N}";

    private sealed record DataKey(int Version, byte[] Material);
}

/// <summary>
/// Wraps and unwraps client data keys under a named master key (AES-256-GCM; client, key version and key id are authenticated, so a
/// wrapped key cannot be moved or re-labelled). The key id is an explicit argument: reads use the id stored in the row, never "whatever is current".
/// </summary>
public static class DataKeyWrapper
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static byte[] Wrap(MasterKeyProvider master, string keyId, Guid clientId, int version, byte[] dataKey)
    {
        var wrapped = new byte[NonceSize + TagSize + dataKey.Length];
        RandomNumberGenerator.Fill(wrapped.AsSpan(0, NonceSize));
        using var aes = new AesGcm(master.GetKey(keyId), TagSize);
        aes.Encrypt(wrapped.AsSpan(0, NonceSize), dataKey, wrapped.AsSpan(NonceSize + TagSize), wrapped.AsSpan(NonceSize, TagSize), Aad(clientId, version, keyId));
        return wrapped;
    }

    /// <summary>Throws <see cref="UnknownMasterKeyException"/> for an id outside the ring, <see cref="CryptographicException"/> for a wrong key or tampering.</summary>
    public static byte[] Unwrap(MasterKeyProvider master, string keyId, Guid clientId, int version, byte[] wrapped)
    {
        if (wrapped.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("The wrapped key is malformed.");
        }

        var dataKey = new byte[wrapped.Length - NonceSize - TagSize];
        using var aes = new AesGcm(master.GetKey(keyId), TagSize);
        aes.Decrypt(wrapped.AsSpan(0, NonceSize), wrapped.AsSpan(NonceSize + TagSize), wrapped.AsSpan(NonceSize, TagSize), dataKey, Aad(clientId, version, keyId));
        return dataKey;
    }

    private static byte[] Aad(Guid clientId, int version, string keyId) => Encoding.UTF8.GetBytes($"dek|{clientId:N}|{version}|{keyId}");
}
