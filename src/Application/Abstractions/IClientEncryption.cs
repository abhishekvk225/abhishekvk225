namespace NexaVerify.Application.Abstractions;

/// <summary>
/// Encrypts data under the calling client's own data key (AES-256-GCM). The ciphertext is bound to the client and to a
/// <paramref name="purpose"/> label, so it cannot be decrypted for another client or reused for another kind of data.
/// </summary>
public interface IClientEncryption
{
    Task<byte[]> EncryptAsync(Guid clientId, ReadOnlyMemory<byte> plaintext, string purpose, CancellationToken cancellationToken);

    Task<byte[]> DecryptAsync(Guid clientId, ReadOnlyMemory<byte> payload, string purpose, CancellationToken cancellationToken);

    /// <summary>Irreversibly destroys the client's keys (crypto-shredding): everything encrypted for the client becomes unreadable.</summary>
    Task DestroyKeysAsync(Guid clientId, CancellationToken cancellationToken);
}
