using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace NexaVerify.Infrastructure.Identity;

/// <summary>Loads the ES256 signing key and the set of keys accepted for validation (supports rotation via key ids).</summary>
public sealed class JwtKeyProvider : IDisposable
{
    private readonly ECDsa _signingKey;
    private readonly List<ECDsa> _owned = [];

    public JwtKeyProvider(IOptions<JwtOptions> options, ILogger<JwtKeyProvider> logger)
    {
        var jwt = options.Value;
        _signingKey = ECDsa.Create();
        _owned.Add(_signingKey);

        if (!string.IsNullOrWhiteSpace(jwt.SigningKeyPem))
        {
            _signingKey.ImportFromPem(jwt.SigningKeyPem);
            if (_signingKey.KeySize != 256)
            {
                throw new InvalidOperationException("Jwt:SigningKeyPem must be an ECDSA P-256 key (ES256).");
            }
        }
        else if (jwt.AllowEphemeralKey)
        {
            _signingKey.Dispose();
            _owned.Clear();
            var ephemeral = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _owned.Add(ephemeral);
            _signingKey = ephemeral;
            logger.LogWarning("Using an EPHEMERAL JWT signing key: tokens are invalid after restart and across instances. Never use this in production.");
        }
        else
        {
            throw new InvalidOperationException(
                "Jwt:SigningKeyPem is not configured. Provide an ECDSA P-256 private key PEM through a secret store (or set Jwt:AllowEphemeralKey for local development).");
        }

        SigningKey = new ECDsaSecurityKey(_signingKey) { KeyId = jwt.SigningKeyId };
        SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.EcdsaSha256)
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = true },
        };

        var validation = new List<SecurityKey> { new ECDsaSecurityKey(_signingKey) { KeyId = jwt.SigningKeyId } };
        foreach (var entry in jwt.AdditionalValidationKeys)
        {
            var key = ECDsa.Create();
            key.ImportFromPem(entry.PublicKeyPem);
            _owned.Add(key);
            validation.Add(new ECDsaSecurityKey(key) { KeyId = entry.KeyId });
        }

        ValidationKeys = validation;
    }

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
}
