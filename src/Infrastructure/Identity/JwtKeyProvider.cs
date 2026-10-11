using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace NexaVerify.Infrastructure.Identity;

/// <summary>
/// Loads the ES256 keyring: signs with the current key, accepts every key in the ring for validation (so tokens signed before a rotation
/// stay valid until they expire). The legacy single-key settings are ring entries too.
/// </summary>
public sealed class JwtKeyProvider : IDisposable
{
    private readonly List<ECDsa> _owned = [];

    public JwtKeyProvider(IOptions<JwtOptions> options, ILogger<JwtKeyProvider> logger)
    {
        var jwt = options.Value;
        var validation = new Dictionary<string, SecurityKey>(StringComparer.Ordinal);
        var privateKeys = new Dictionary<string, ECDsa>(StringComparer.Ordinal);

        void Register(string path, string keyId, string? privatePem, string? publicPem)
        {
            if (string.IsNullOrWhiteSpace(keyId))
            {
                throw new InvalidOperationException($"{path}: a key id is required.");
            }

            if (validation.ContainsKey(keyId))
            {
                throw new InvalidOperationException($"{path}: duplicate JWT key id '{keyId}'.");
            }

            var key = ECDsa.Create();
            _owned.Add(key);
            try
            {
                if (!string.IsNullOrWhiteSpace(privatePem))
                {
                    key.ImportFromPem(privatePem);
                    privateKeys[keyId] = key;
                }
                else if (!string.IsNullOrWhiteSpace(publicPem))
                {
                    key.ImportFromPem(publicPem);
                }
                else
                {
                    throw new InvalidOperationException($"{path}: key '{keyId}' has neither a signing key nor a public key.");
                }
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                throw new InvalidOperationException($"{path}: the PEM for JWT key '{keyId}' could not be read ({ex.GetType().Name}).");
            }

            if (key.KeySize != 256)
            {
                throw new InvalidOperationException($"{path}: JWT key '{keyId}' must be an ECDSA P-256 key (ES256).");
            }

            validation[keyId] = new ECDsaSecurityKey(key) { KeyId = keyId };
            logger.LogInformation("JWT keyring: key {KeyId} fingerprint {Fingerprint} ({Kind})", keyId, Fingerprint(key), privateKeys.ContainsKey(keyId) ? "signing" : "validation only");
        }

        if (!string.IsNullOrWhiteSpace(jwt.SigningKeyPem))
        {
            Register("Jwt:SigningKeyPem", jwt.SigningKeyId, jwt.SigningKeyPem, null);
        }

        for (var i = 0; i < jwt.Keys.Count; i++)
        {
            var entry = jwt.Keys[i];
            Register($"Jwt:Keys:{i}", entry.KeyId, entry.SigningKeyPem, entry.PublicKeyPem);
        }

        for (var i = 0; i < jwt.AdditionalValidationKeys.Count; i++)
        {
            Register($"Jwt:AdditionalValidationKeys:{i}", jwt.AdditionalValidationKeys[i].KeyId, null, jwt.AdditionalValidationKeys[i].PublicKeyPem);
        }

        string currentId;
        if (validation.Count == 0)
        {
            if (!jwt.AllowEphemeralKey)
            {
                throw new InvalidOperationException(
                    "No JWT signing key is configured. Provide an ECDSA P-256 private key PEM (Jwt:SigningKeyPem, or Jwt:Keys with Jwt:CurrentKeyId) through a secret store (or set Jwt:AllowEphemeralKey for local development).");
            }

            var ephemeral = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _owned.Add(ephemeral);
            privateKeys[jwt.SigningKeyId] = ephemeral;
            validation[jwt.SigningKeyId] = new ECDsaSecurityKey(ephemeral) { KeyId = jwt.SigningKeyId };
            currentId = jwt.SigningKeyId;
            logger.LogWarning("Using an EPHEMERAL JWT signing key: tokens are invalid after restart and across instances. Never use this in production.");
        }
        else
        {
            currentId = !string.IsNullOrWhiteSpace(jwt.CurrentKeyId) ? jwt.CurrentKeyId
                : validation.ContainsKey(jwt.SigningKeyId) ? jwt.SigningKeyId
                : validation.Count == 1 ? validation.Keys.Single()
                : throw new InvalidOperationException("Jwt:CurrentKeyId is required when the JWT keyring holds several keys.");
            if (!validation.ContainsKey(currentId))
            {
                throw new InvalidOperationException($"Jwt:CurrentKeyId '{currentId}' is not in the JWT keyring.");
            }

            if (!privateKeys.ContainsKey(currentId))
            {
                throw new InvalidOperationException($"Jwt:CurrentKeyId '{currentId}' has no private signing key (SigningKeyPem): it can only validate.");
            }
        }

        CurrentKeyId = currentId;
        SigningKey = new ECDsaSecurityKey(privateKeys[currentId]) { KeyId = currentId };
        SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.EcdsaSha256)
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = true },
        };
        ValidationKeys = validation.Values.ToList();
        KeyIds = validation.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    public string CurrentKeyId { get; }

    public IReadOnlyList<string> KeyIds { get; }

    public SecurityKey SigningKey { get; }

    public SigningCredentials SigningCredentials { get; }

    public IReadOnlyList<SecurityKey> ValidationKeys { get; }

    public void Dispose()
    {
        foreach (var key in _owned)
        {
            key.Dispose();
        }
    }

    private static string Fingerprint(ECDsa key) => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..8].ToLowerInvariant();
}
