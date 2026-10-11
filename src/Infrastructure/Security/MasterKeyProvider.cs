using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NexaVerify.Infrastructure.Security;

/// <summary>One master key in the keyring: an id (stored with everything the key protects) and the Base64 of its 32 bytes (a secret).</summary>
public sealed class EncryptionKeyEntry
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Base64 of exactly 32 random bytes. Supply through a secret store / secrets directory, never appsettings.</summary>
    public string? KeyBase64 { get; set; }
}

public sealed class EncryptionOptions
{
    public const string SectionName = "Encryption";

    /// <summary>
    /// The keyring: every master key that may still be needed to read existing data. Encryption always uses <see cref="CurrentKeyId"/>;
    /// decryption uses the key named by the record itself. Replaces the single-key settings below (which keep working).
    /// </summary>
    public List<EncryptionKeyEntry> Keys { get; set; } = [];

    /// <summary>Id of the ring entry used to encrypt, wrap and sign new data. Defaults to <see cref="MasterKeyId"/> when that is in the ring, or to the only key.</summary>
    public string? CurrentKeyId { get; set; }

    /// <summary>
    /// Legacy single-key setting (still supported): Base64 of the 32-byte master key. Treated as a keyring entry named
    /// <see cref="MasterKeyId"/>. Data written before the keyring existed carries no key id and is read with this entry.
    /// </summary>
    public string? MasterKeyBase64 { get; set; }

    /// <summary>Id of the legacy key (<c>m1</c> unless the deployment chose another). Also the key that unmarked, pre-keyring payloads belong to.</summary>
    [Required]
    public string MasterKeyId { get; set; } = "m1";

    /// <summary>Re-wraps data protected by a non-current key in small background batches after <see cref="CurrentKeyId"/> changes. Off by default; the Migrator command is the supported path.</summary>
    public bool LazyRewrap { get; set; }

    /// <summary>Seconds between lazy re-wrap batches.</summary>
    [Range(5, 86_400)]
    public int LazyRewrapIntervalSeconds { get; set; } = 30;

    /// <summary>Rows per lazy re-wrap batch.</summary>
    [Range(1, 1000)]
    public int LazyRewrapBatchSize { get; set; } = 50;

    /// <summary>Development/test only: generate a throw-away master key (data does not survive a restart).</summary>
    public bool AllowEphemeralKey { get; set; }
}

/// <summary>A key id was not found in the keyring. Names the id (never key material).</summary>
public sealed class UnknownMasterKeyException : CryptographicException
{
    public UnknownMasterKeyException(string keyId)
        : base($"Master key id '{keyId}' is not in the keyring (Encryption:Keys). The data was protected with a key that is not configured; add that key to the ring, do not substitute another.")
    {
        KeyId = keyId;
    }

    public string KeyId { get; }
}

/// <summary>Parsed, validated keyring. Shared by startup validation and <see cref="MasterKeyProvider"/>.</summary>
internal sealed class KeyringConfiguration
{
    public const int KeySize = 32;
    public const int MaxKeyIdLength = 50; // tenancy.ClientKeys.MasterKeyId is nvarchar(50)

    private static readonly Regex IdPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public required IReadOnlyDictionary<string, byte[]> Keys { get; init; }

    public required string CurrentKeyId { get; init; }

    public required string LegacyKeyId { get; init; }

    public bool Ephemeral { get; init; }

    public static (KeyringConfiguration? Config, IReadOnlyList<string> Errors) Parse(EncryptionOptions o)
    {
        var errors = new List<string>();
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        void Add(string path, string? id, string? base64)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > MaxKeyIdLength || !IdPattern.IsMatch(id))
            {
                errors.Add($"{path}: the key id must be 1-{MaxKeyIdLength} characters of letters, digits, '.', '_' or '-'.");
                return;
            }

            if (keys.ContainsKey(id))
            {
                errors.Add($"{path}: duplicate key id '{id}'.");
                return;
            }

            if (string.IsNullOrWhiteSpace(base64))
            {
                errors.Add($"{path}: no key material for '{id}' (supply the Base64 key through a secret store).");
                return;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64.Trim());
            }
            catch (FormatException)
            {
                errors.Add($"{path}: the key for '{id}' is not valid Base64.");
                return;
            }

            if (bytes.Length != KeySize)
            {
                errors.Add($"{path}: the key for '{id}' must decode to exactly {KeySize} bytes (got {bytes.Length}).");
                return;
            }

            keys[id] = bytes;
        }

        var hasLegacy = !string.IsNullOrWhiteSpace(o.MasterKeyBase64);
        if (hasLegacy)
        {
            Add("Encryption:MasterKeyBase64", o.MasterKeyId, o.MasterKeyBase64);
        }

        for (var i = 0; i < o.Keys.Count; i++)
        {
            var entry = o.Keys[i];
            if (hasLegacy && string.Equals(entry.Id, o.MasterKeyId, StringComparison.Ordinal) && keys.TryGetValue(entry.Id, out var legacy))
            {
                // The same key listed twice (old setting kept next to the ring) is fine if it is the same material.
                if (!string.IsNullOrWhiteSpace(entry.KeyBase64) && !TryDecodesTo(entry.KeyBase64, legacy))
                {
                    errors.Add($"Encryption:Keys:{i}: id '{entry.Id}' is also the legacy MasterKeyId but holds different key material.");
                }

                continue;
            }

            Add($"Encryption:Keys:{i}", entry.Id, entry.KeyBase64);
        }

        var ephemeral = false;
        if (keys.Count == 0 && errors.Count == 0)
        {
            if (o.AllowEphemeralKey)
            {
                keys[o.MasterKeyId] = RandomNumberGenerator.GetBytes(KeySize);
                ephemeral = true;
            }
            else
            {
                errors.Add("No master key is configured: set Encryption:Keys (with Encryption:CurrentKeyId) or the legacy Encryption:MasterKeyBase64, through a secret store.");
            }
        }

        var distinct = keys.GroupBy(k => Convert.ToHexString(SHA256.HashData(k.Value))).Where(g => g.Count() > 1).ToList();
        foreach (var group in distinct)
        {
            errors.Add($"Keys {string.Join(", ", group.Select(g => $"'{g.Key}'"))} hold identical key material: a rotation needs a new random key.");
        }

        string? current = null;
        if (errors.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(o.CurrentKeyId))
            {
                current = o.CurrentKeyId;
                if (!keys.ContainsKey(current))
                {
                    errors.Add($"Encryption:CurrentKeyId '{current}' is not in the keyring.");
                }
            }
            else if (keys.Count == 1)
            {
                current = keys.Keys.Single();
            }
            else if (keys.ContainsKey(o.MasterKeyId))
            {
                current = o.MasterKeyId; // a ring that still holds the legacy key stays on it until CurrentKeyId says otherwise
            }
            else
            {
                errors.Add("Encryption:CurrentKeyId is required when the keyring holds several keys.");
            }
        }

        return errors.Count > 0
            ? (null, errors)
            : (new KeyringConfiguration { Keys = keys, CurrentKeyId = current!, LegacyKeyId = o.MasterKeyId, Ephemeral = ephemeral }, errors);
    }

    private static bool TryDecodesTo(string base64, byte[] expected)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(base64.Trim()), expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Fails application start when the keyring is unusable (reports paths and ids, never key material).</summary>
public sealed class EncryptionOptionsValidator : IValidateOptions<EncryptionOptions>
{
    public ValidateOptionsResult Validate(string? name, EncryptionOptions options)
    {
        var (_, errors) = KeyringConfiguration.Parse(options);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>What the keyring knows about one key: id, short fingerprint (safe to log) and whether it is the encryption key.</summary>
public sealed record MasterKeyInfo(string Id, string Fingerprint, bool IsCurrent);

/// <summary>
/// The master keyring. Encrypt/wrap/sign with <see cref="CurrentKeyId"/>; decrypt/verify with the key a record names. An unknown id is
/// always an error (<see cref="UnknownMasterKeyException"/>), never a fallback to another key. Key bytes never leave the assembly.
/// </summary>
public sealed class MasterKeyProvider
{
    private readonly IReadOnlyDictionary<string, byte[]> _keys;

    public MasterKeyProvider(IOptions<EncryptionOptions> options, ILogger<MasterKeyProvider> logger)
    {
        var (config, errors) = KeyringConfiguration.Parse(options.Value);
        if (config is null)
        {
            throw new InvalidOperationException("The master keyring is invalid: " + string.Join(" ", errors));
        }

        _keys = config.Keys;
        CurrentKeyId = config.CurrentKeyId;
        LegacyKeyId = config.LegacyKeyId;
        IsEphemeral = config.Ephemeral;
        Keys = config.Keys.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Select(id => new MasterKeyInfo(id, Fingerprint(config.Keys[id]), id == CurrentKeyId))
            .ToList();

        if (IsEphemeral)
        {
            logger.LogWarning("Using an EPHEMERAL master key: encrypted data is unreadable after restart. Never use this in production.");
        }

        foreach (var key in Keys)
        {
            logger.LogInformation("Master keyring: key {KeyId} fingerprint {Fingerprint}{Current}", key.Id, key.Fingerprint, key.IsCurrent ? " (current)" : string.Empty);
        }
    }

    /// <summary>The key used to encrypt, wrap and sign new data.</summary>
    public string CurrentKeyId { get; }

    /// <summary>Alias of <see cref="CurrentKeyId"/> (the id stored with newly written data).</summary>
    public string MasterKeyId => CurrentKeyId;

    /// <summary>The key id that data written before the keyring existed (no recorded id) belongs to.</summary>
    public string LegacyKeyId { get; }

    public bool IsEphemeral { get; }

    public IReadOnlyList<MasterKeyInfo> Keys { get; }

    public bool Contains(string keyId) => _keys.ContainsKey(keyId);

    /// <summary>First 8 hex characters of SHA-256(key): identifies a key in logs and reports without revealing it.</summary>
    public static string Fingerprint(byte[] key) => Convert.ToHexString(SHA256.HashData(key))[..8].ToLowerInvariant();

    internal byte[] Key => _keys[CurrentKeyId];

    internal byte[] GetKey(string keyId) =>
        _keys.TryGetValue(keyId, out var key) ? key : throw new UnknownMasterKeyException(keyId);
}
