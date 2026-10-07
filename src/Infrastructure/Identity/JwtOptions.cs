using System.ComponentModel.DataAnnotations;

namespace NexaVerify.Infrastructure.Identity;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = "nexaverify";

    [Required]
    public string Audience { get; set; } = "nexaverify-api";

    [Range(1, 120)]
    public int AccessTokenMinutes { get; set; } = 15;

    [Range(0, 300)]
    public int ClockSkewSeconds { get; set; } = 60;

    /// <summary>
    /// PEM of the ECDSA P-256 private key used to sign (ES256). A secret: supply via user-secrets / environment / key vault,
    /// never appsettings. Required unless <see cref="AllowEphemeralKey"/> is set.
    /// </summary>
    public string? SigningKeyPem { get; set; }

    /// <summary>Key id placed in the token header; bump it when rotating.</summary>
    [Required]
    public string SigningKeyId { get; set; } = "k1";

    /// <summary>Public keys of previous signing keys, still accepted for validation during rotation.</summary>
    public List<PublicKeyEntry> AdditionalValidationKeys { get; set; } = [];

    /// <summary>Development/test only: generate a throw-away key at startup (tokens die on restart, and break across instances).</summary>
    public bool AllowEphemeralKey { get; set; }

    public sealed class PublicKeyEntry
    {
        public string KeyId { get; set; } = string.Empty;

        public string PublicKeyPem { get; set; } = string.Empty;
    }
}
