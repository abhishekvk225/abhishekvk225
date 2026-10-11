using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using NexaVerify.Application.Abstractions;

namespace NexaVerify.Infrastructure.Security;

/// <summary>
/// Purpose-separated keys derived from the master keyring with HKDF-SHA256 (RFC 5869). Derived keys are cached in memory for the life
/// of the process; master keys are only ever read through <see cref="MasterKeyProvider"/>.
/// Payload layout: <see cref="KeyEnvelope"/> header (names the key) | nonce(12) | tag(16) | ciphertext. Payloads without a header are
/// pre-keyring data and are read with the legacy key (and its original authenticated data); they are upgraded by a key rotation.
/// </summary>
public sealed class PlatformCrypto : IPlatformCrypto
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly MasterKeyProvider _master;
    private readonly ConcurrentDictionary<(string KeyId, string Purpose), byte[]> _keys = new();

    public PlatformCrypto(MasterKeyProvider master)
    {
        _master = master;
    }

    public string KeyId => _master.CurrentKeyId;

    public bool HasKey(string keyId) => _master.Contains(keyId);

    public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose, string context)
    {
        var keyId = _master.CurrentKeyId;
        var header = KeyEnvelope.HeaderLength(keyId);
        var payload = new byte[header + NonceSize + TagSize + plaintext.Length];
        KeyEnvelope.WriteHeader(keyId, payload);
        RandomNumberGenerator.Fill(payload.AsSpan(header, NonceSize));
        using var aes = new AesGcm(KeyFor(keyId, purpose), TagSize);
        aes.Encrypt(
            payload.AsSpan(header, NonceSize), plaintext, payload.AsSpan(header + NonceSize + TagSize), payload.AsSpan(header + NonceSize, TagSize), Aad(purpose, context, keyId));
        return payload;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> payload, string purpose, string context)
    {
        if (KeyEnvelope.TryReadHeader(payload, out var keyId, out var offset) && payload.Length >= offset + NonceSize + TagSize)
        {
            try
            {
                return Open(payload[offset..], KeyFor(keyId, purpose), Aad(purpose, context, keyId));
            }
            catch (UnknownMasterKeyException)
            {
                throw;
            }
            catch (CryptographicException) when (LegacyFallbackPossible(payload))
            {
                // A pre-keyring payload whose random nonce happens to begin like a header: read it as legacy.
            }
        }

        if (payload.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Ciphertext is malformed.");
        }

        return Open(payload, KeyFor(_master.LegacyKeyId, purpose), LegacyAad(purpose, context));
    }

    /// <summary>The id of the key that protected <paramref name="payload"/> (the legacy id for pre-keyring payloads). Does not verify the payload.</summary>
    public string KeyIdOf(ReadOnlySpan<byte> payload) =>
        KeyEnvelope.TryReadHeader(payload, out var keyId, out _) ? keyId : _master.LegacyKeyId;

    /// <summary>Decrypts with the key the payload names and seals again under the current key. Plaintext only lives inside this call.</summary>
    public byte[] Reprotect(ReadOnlySpan<byte> payload, string purpose, string context)
    {
        var plaintext = Unprotect(payload, purpose, context);
        try
        {
            return Protect(plaintext, purpose, context);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public byte[] ComputeMac(string purpose, ReadOnlySpan<byte> data) => ComputeMac(purpose, data, _master.CurrentKeyId);

    public byte[] ComputeMac(string purpose, ReadOnlySpan<byte> data, string keyId) => HMACSHA256.HashData(KeyFor(keyId, purpose), data);

    private bool LegacyFallbackPossible(ReadOnlySpan<byte> payload) => payload.Length >= NonceSize + TagSize && _master.Contains(_master.LegacyKeyId);

    private static byte[] Open(ReadOnlySpan<byte> sealedBody, byte[] key, byte[] aad)
    {
        var plaintext = new byte[sealedBody.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(sealedBody[..NonceSize], sealedBody[(NonceSize + TagSize)..], sealedBody.Slice(NonceSize, TagSize), plaintext, aad);
        return plaintext;
    }

    private byte[] KeyFor(string keyId, string purpose)
    {
        var master = _master.GetKey(keyId); // throws UnknownMasterKeyException: an unknown id never falls back to another key
        return _keys.GetOrAdd((keyId, purpose), _ => HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt: null, info: Encoding.UTF8.GetBytes("nexaverify/platform/" + purpose)));
    }

    private static byte[] Aad(string purpose, string context, string keyId) => Encoding.UTF8.GetBytes($"plat2|{purpose}|{context}|{keyId}");

    private static byte[] LegacyAad(string purpose, string context) => Encoding.UTF8.GetBytes($"plat|{purpose}|{context}");
}
