using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NexaVerify.Infrastructure.Security;

namespace NexaVerify.Infrastructure.Platform.KeyRotation;

/// <summary>
/// Readiness: the keyring is loaded (otherwise the host would not have started) and no live data references a master key id the ring
/// does not hold. Unhealthy then, never a silent fallback to another key. Degraded (still ready) while data waits for re-keying after a
/// CurrentKeyId change. Reports key ids and booleans only: no key material, no fingerprints, no counts. The survey is cached for a few minutes.
/// </summary>
public sealed partial class KeyringHealthCheck : IHealthCheck
{
    public const string Name = "keyring";

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly KeyUsageSurvey _survey;
    private readonly MasterKeyProvider _master;
    private readonly IMemoryCache _cache;
    private readonly ILogger<KeyringHealthCheck> _logger;

    public KeyringHealthCheck(KeyUsageSurvey survey, MasterKeyProvider master, IMemoryCache cache, ILogger<KeyringHealthCheck> logger)
    {
        _survey = survey;
        _master = master;
        _cache = cache;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["currentKeyId"] = _master.CurrentKeyId,
            ["keyIds"] = _master.Keys.Select(k => k.Id).ToArray(),
        };

        KeyUsageReport usage;
        try
        {
            usage = (await _cache.GetOrCreateAsync("keyring-usage", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheFor;
                return await _survey.SurveyAsync(cancellationToken);
            }))!;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSurveyFailed(_logger, ex.GetType().Name);
            data["unknownKeyIdsInUse"] = Array.Empty<string>();
            return HealthCheckResult.Unhealthy("The keyring usage could not be checked.", data: data);
        }

        var unknown = usage.UnknownKeyIds;
        data["unknownKeyIdsInUse"] = unknown.ToArray();
        data["rewrapPending"] = usage.PendingTotal > 0;
        if (unknown.Count > 0)
        {
            LogUnknownKeys(_logger, string.Join(", ", unknown));
            return HealthCheckResult.Unhealthy("Stored data references a master key id that is not in the keyring.", data: data);
        }

        return usage.PendingTotal > 0
            ? HealthCheckResult.Degraded("Some data is still protected by an older master key; run the key rotation.", data: data)
            : HealthCheckResult.Healthy("Keyring is consistent.", data);
    }

    [LoggerMessage(EventId = 7110, Level = LogLevel.Critical, Message = "KEYRING MISMATCH: stored data references master key id(s) [{KeyIds}] that are not in Encryption:Keys. That data cannot be read until the key is added back.")]
    private static partial void LogUnknownKeys(ILogger logger, string keyIds);

    [LoggerMessage(EventId = 7111, Level = LogLevel.Error, Message = "The keyring usage survey failed ({ErrorType})")]
    private static partial void LogSurveyFailed(ILogger logger, string errorType);
}
