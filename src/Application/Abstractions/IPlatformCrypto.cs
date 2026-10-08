namespace NexaVerify.Application.Abstractions;

/// <summary>
/// Platform-level cryptography for data that belongs to no single client (staff MFA secrets, the ledger anchor MAC). Keys are
/// derived per <c>purpose</c> from the master key provider, so a key used for one purpose can never decrypt or authenticate another.
/// </summary>
public interface IPlatformCrypto
{
    /// <summary>AES-256-GCM. <paramref name="context"/> (for example the owning user id) is authenticated, so a ciphertext cannot be moved to another record.</summary>
    byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose, string context);

    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> on any tampering, wrong purpose or wrong context.</summary>
    byte[] Unprotect(ReadOnlySpan<byte> payload, string purpose, string context);

    /// <summary>HMAC-SHA256 under the purpose's derived key. The key never leaves the process and is not stored in the database.</summary>
    byte[] ComputeMac(string purpose, ReadOnlySpan<byte> data);
}

/// <summary>Purpose labels for <see cref="IPlatformCrypto"/> (each yields an independent key).</summary>
public static class CryptoPurposes
{
    public const string MfaSecret = "mfa-secret";
    public const string LedgerAnchor = "ledger-anchor";
}
