using System.Buffers.Binary;
using System.ComponentModel.DataAnnotations;
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

public sealed class EncryptionOptions
{
    public const string SectionName = "Encryption";

    /// <summary>Base64 of the 32-byte master key (KEK) that wraps every client's data key. A secret — supply via a key vault / secret store.</summary>
    public string? MasterKeyBase64 { get; set; }

    /// <summary>Identifier stored with wrapped keys; bump when the master key is rotated.</summary>
    [Required]
    public string MasterKeyId { get; set; } = "m1";

    /// <summary>Development/test only: generate a throw-away master key (data does not survive a restart).</summary>
    public bool AllowEphemeralKey { get; set; }
}

public sealed class MasterKeyProvider
{
    public MasterKeyProvider(IOptions<EncryptionOptions> options, ILogger<MasterKeyProvider> logger)
    {
        var o = options.Value;
        MasterKeyId = o.MasterKeyId;
        if (!string.IsNullOrWhiteSpace(o.MasterKeyBase64))
        {
            Key = Convert.FromBase64String(o.MasterKeyBase64);
            if (Key.Length != 32)
            {
                throw new InvalidOperationException("Encryption:MasterKeyBase64 must decode to exactly 32 bytes.");
            }
        }
        else if (o.AllowEphemeralKey)
        {
            Key = RandomNumberGenerator.GetBytes(32);
            logger.LogWarning("Using an EPHEMERAL master key: encrypted data is unreadable after restart. Never use this in production.");
        }
        else
        {
            throw new InvalidOperationException("Encryption:MasterKeyBase64 is not configured. Provide a 32-byte base64 key through a secret store.");
        }
    }

    public string MasterKeyId { get; }

    internal byte[] Key { get; }
}

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
            _db.ClientKeys.Add(ClientKey.Create(clientId, 1, Wrap(clientId, 1, dataKey), _master.MasterKeyId, _time.GetUtcNow().UtcDateTime));
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

            return new DataKey(version, Unwrap(clientId, version, row.WrappedDataKey));
        });

        return key!;
    }

    private byte[] Wrap(Guid clientId, int version, byte[] dataKey)
    {
        var wrapped = new byte[NonceSize + TagSize + dataKey.Length];
        RandomNumberGenerator.Fill(wrapped.AsSpan(0, NonceSize));
        using var aes = new AesGcm(_master.Key, TagSize);
        aes.Encrypt(wrapped.AsSpan(0, NonceSize), dataKey, wrapped.AsSpan(NonceSize + TagSize), wrapped.AsSpan(NonceSize, TagSize), WrapAad(clientId, version));
        return wrapped;
    }

    private byte[] Unwrap(Guid clientId, int version, byte[] wrapped)
    {
        var dataKey = new byte[wrapped.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_master.Key, TagSize);
        aes.Decrypt(wrapped.AsSpan(0, NonceSize), wrapped.AsSpan(NonceSize + TagSize), wrapped.AsSpan(NonceSize, TagSize), dataKey, WrapAad(clientId, version));
        return dataKey;
    }

    private byte[] WrapAad(Guid clientId, int version) => Encoding.UTF8.GetBytes($"dek|{clientId:N}|{version}|{_master.MasterKeyId}");

    private static byte[] Aad(Guid clientId, string purpose, int version) => Encoding.UTF8.GetBytes($"data|{clientId:N}|{purpose}|{version}");

    private static string CacheKey(Guid clientId, int version) => $"dek:{clientId:N}:{version}";

    private static string ActiveKey(Guid clientId) => $"dek-active:{clientId:N}";

    private sealed record DataKey(int Version, byte[] Material);
}
