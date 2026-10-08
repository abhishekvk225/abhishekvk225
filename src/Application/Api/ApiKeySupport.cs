using System.Net;
using System.Security.Cryptography;
using NexaVerify.Application.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Domain.Api;

namespace NexaVerify.Application.Api;

/// <summary>Key format: <c>nxv_live_{8 chars}_{43 chars}</c> — a public prefix (identifier) and a 256-bit random secret.</summary>
public static class ApiKeyMaterial
{
    public const string Marker = "nxv_live_";
    public const int PrefixLength = 17; // marker + 8
    public const int KeyLength = PrefixLength + 1 + 43;

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public static (string Raw, string Prefix, byte[] Hash) Generate()
    {
        var id = new string(Enumerable.Range(0, 8).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
        var prefix = Marker + id;
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var raw = prefix + "_" + secret;
        return (raw, prefix, Hash(raw));
    }

    public static byte[] Hash(string raw) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));

    /// <summary>Returns the prefix when <paramref name="raw"/> has the right shape, otherwise null (the secret is never inspected here).</summary>
    public static string? ParsePrefix(string? raw) =>
        raw is { Length: KeyLength } && raw.StartsWith(Marker, StringComparison.Ordinal) && raw[PrefixLength] == '_' ? raw[..PrefixLength] : null;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The scopes an API key may carry: integration operations only, never account administration.</summary>
public static class ApiKeyScopes
{
    public static IReadOnlyList<string> Assignable { get; } =
    [
        Permissions.Faces.Enroll,
        Permissions.Faces.Verify,
        Permissions.Faces.Identify,
        Permissions.Faces.Detect,
        Permissions.Faces.Read,
        Permissions.Faces.Manage,
        Permissions.Faces.Erase,
        Permissions.Faces.History,
    ];

    public static string Describe(string key) => Permissions.All.FirstOrDefault(p => p.Key == key)?.Description ?? key;
}

public static class IpRules
{
    /// <summary>A single address or a CIDR range such as <c>203.0.113.0/24</c>.</summary>
    public static bool IsValid(string rule)
    {
        var parts = rule.Trim().Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address))
        {
            return false;
        }

        if (parts.Length == 1)
        {
            return true;
        }

        var max = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        // IPNetwork rejects host bits (203.0.113.7/24): a rule like that would silently never match, so refuse it up front.
        return int.TryParse(parts[1], out var prefix) && prefix >= 0 && prefix <= max && System.Net.IPNetwork.TryParse(rule.Trim(), out _);
    }

    /// <summary>True when the list is empty (no restriction) or the address matches one entry.</summary>
    public static bool Allows(IReadOnlyCollection<string> rules, string? address)
    {
        if (rules.Count == 0)
        {
            return true;
        }

        if (!IPAddress.TryParse(address, out var ip))
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        foreach (var rule in rules)
        {
            var parts = rule.Split('/');
            if (!IPAddress.TryParse(parts[0], out var net))
            {
                continue;
            }

            if (net.IsIPv4MappedToIPv6)
            {
                net = net.MapToIPv4();
            }

            if (parts.Length == 1 ? net.Equals(ip) : System.Net.IPNetwork.TryParse(rule, out var network) && network.Contains(ip))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>What a successful key authentication proves.</summary>
public sealed record ApiKeyIdentity(Guid ClientId, Guid KeyId, IReadOnlyList<string> Scopes, int? RateLimitPerMinute);

public interface IApiKeyAuthenticator
{
    /// <summary>
    /// Validates a raw key presented by a caller. Unknown prefix and wrong secret are indistinguishable (same error); revoked,
    /// expired, wrong-IP and suspended-client keys are only reported after the secret has been proven.
    /// </summary>
    Task<Result<ApiKeyIdentity>> AuthenticateAsync(string rawKey, string? ipAddress, CancellationToken cancellationToken);

    /// <summary>Drops cached knowledge of a key so a revoke/update applies on the very next request on this node.</summary>
    void Invalidate(string prefix);
}

/// <summary>Names of the per-principal throttles (limits live in configuration, not here).</summary>
public static class ThrottlePolicies
{
    public const string Dashboards = "dashboards";
    public const string Exports = "exports";
    public const string WebhookTest = "webhook-test";
    public const string WebhookRetry = "webhook-retry";
}

/// <summary>Per-principal (signed-in user or API key) throttle on expensive operations; shared across API nodes.</summary>
public interface IPrincipalThrottle
{
    /// <summary>Returns null when the call may proceed, otherwise the 429 error and how long to wait.</summary>
    Task<(Error? Error, TimeSpan RetryAfter)> TryAcquireAsync(string policy, Guid principalId, CancellationToken cancellationToken);
}

/// <summary>Per-credential rate limit and per-client daily quota.</summary>
public interface IApiUsageLimiter
{
    /// <summary>Returns null when the call may proceed, otherwise the 429 error and how long to wait.</summary>
    Task<(Error? Error, TimeSpan RetryAfter)> TryAcquireAsync(Guid clientId, Guid credentialId, int? credentialLimitPerMinute, CancellationToken cancellationToken);
}

public sealed record ApiRequestLogEntry(
    Guid ClientId, Guid? ApiKeyId, Guid? UserId, string Method, string Route, int StatusCode, int DurationMs, long? RequestBytes,
    string? IpAddress, string? UserAgent, string? ErrorCode, string? CorrelationId, DateTime OccurredAt);

/// <summary>Fire-and-forget: a slow database must never slow the API down, so the log is queued and written in batches.</summary>
public interface IApiRequestLogSink
{
    void Enqueue(ApiRequestLogEntry entry);
}
