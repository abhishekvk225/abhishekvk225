using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using NexaVerify.Application.Abstractions;

namespace NexaVerify.Infrastructure.Security;

/// <summary>
/// Purpose-separated keys derived from the master key with HKDF-SHA256 (RFC 5869). The derived keys are cached in memory for the
/// life of the process; the master key itself is only ever read through <see cref="MasterKeyProvider"/>.
/// Payload layout: nonce(12) | tag(16) | ciphertext.
/// </summary>
public sealed class PlatformCrypto : IPlatformCrypto
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly MasterKeyProvider _master;
    private readonly ConcurrentDictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public PlatformCrypto(MasterKeyProvider master)
    {
        _master = master;
    }

    public string KeyId => _master.MasterKeyId;

    public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose, string context)
    {
        var payload = new byte[NonceSize + TagSize + plaintext.Length];
        RandomNumberGenerator.Fill(payload.AsSpan(0, NonceSize));
        using var aes = new AesGcm(KeyFor(purpose), TagSize);
        aes.Encrypt(payload.AsSpan(0, NonceSize), plaintext, payload.AsSpan(NonceSize + TagSize), payload.AsSpan(NonceSize, TagSize), Aad(purpose, context));
        return payload;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> payload, string purpose, string context)
    {
        if (payload.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Ciphertext is malformed.");
        }

        var plaintext = new byte[payload.Length - NonceSize - TagSize];
        using var aes = new AesGcm(KeyFor(purpose), TagSize);
        aes.Decrypt(payload[..NonceSize], payload[(NonceSize + TagSize)..], payload.Slice(NonceSize, TagSize), plaintext, Aad(purpose, context));
        return plaintext;
    }

    public byte[] ComputeMac(string purpose, ReadOnlySpan<byte> data) => HMACSHA256.HashData(KeyFor(purpose), data);

    private byte[] KeyFor(string purpose) =>
        _keys.GetOrAdd(purpose, p => HKDF.DeriveKey(HashAlgorithmName.SHA256, _master.Key, 32, salt: null, info: Encoding.UTF8.GetBytes("nexaverify/platform/" + p)));

    private static byte[] Aad(string purpose, string context) => Encoding.UTF8.GetBytes($"plat|{purpose}|{context}");
}
