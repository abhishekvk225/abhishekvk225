using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Application.Abstractions;
using NexaVerify.Domain.Tenancy;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.Infrastructure.Security;

namespace NexaVerify.Infrastructure.Platform.KeyRotation;

/// <summary>The kinds of data a master key protects (and therefore the things a rotation must re-key).</summary>
public static class KeyArtifacts
{
    /// <summary>tenancy.ClientKeys: each client's data key, wrapped by a master key. Face templates and webhook secrets sit under these (their ciphertext never changes).</summary>
    public const string ClientDataKey = "ClientDataKey";

    /// <summary>iam.UserMfa: TOTP secrets, sealed with a key derived from a master key.</summary>
    public const string MfaSecret = "MfaSecret";

    /// <summary>licensing.LedgerCheckpoints that are not superseded: HMAC-signed with a key derived from a master key.</summary>
    public const string LedgerCheckpoint = "LedgerCheckpoint";

    public static readonly IReadOnlyList<string> All = [ClientDataKey, MfaSecret, LedgerCheckpoint];
}

/// <summary>How many live artifacts of each kind are protected by each key id. Counts only: no ids of rows, no key material.</summary>
public sealed class KeyUsageReport
{
    public KeyUsageReport(string currentKeyId, IReadOnlyList<string> ringKeyIds, IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> counts)
    {
        CurrentKeyId = currentKeyId;
        RingKeyIds = ringKeyIds;
        Counts = counts;
    }

    public string CurrentKeyId { get; }

    public IReadOnlyList<string> RingKeyIds { get; }

    /// <summary>artifact kind -> key id -> number of artifacts.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> Counts { get; }

    public long Total(string artifact) => Counts.TryGetValue(artifact, out var byKey) ? byKey.Values.Sum() : 0;

    public long Under(string artifact, string keyId) => Counts.TryGetValue(artifact, out var byKey) && byKey.TryGetValue(keyId, out var n) ? n : 0;

    /// <summary>Artifacts still protected by a key other than the current one.</summary>
    public long Pending(string artifact) => Total(artifact) - Under(artifact, CurrentKeyId);

    public long PendingTotal => KeyArtifacts.All.Sum(Pending);

    public long GrandTotal => KeyArtifacts.All.Sum(Total);

    /// <summary>100 when nothing needs re-keying (including an empty database).</summary>
    public double PercentComplete => GrandTotal == 0 ? 100.0 : Math.Round(100.0 * (GrandTotal - PendingTotal) / GrandTotal, 2);

    /// <summary>Key ids that live data references but the keyring does not hold: that data is unreadable until the key is added back.</summary>
    public IReadOnlyList<string> UnknownKeyIds => Counts.Values.SelectMany(v => v.Keys).Where(id => !RingKeyIds.Contains(id)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

    /// <summary>True when no live artifact references <paramref name="keyId"/>, it is not the current key, and so it can be removed from the ring (keep it vaulted for backups).</summary>
    public bool IsSafeToRetire(string keyId) => keyId != CurrentKeyId && Counts.Values.All(byKey => !byKey.TryGetValue(keyId, out var n) || n == 0);
}

/// <summary>
/// Counts what each master key protects. Reads in platform scope with grouped queries, plus a paged scan of the (few) MFA secrets whose
/// key id sits in a payload header. Used by rotation-status, the rotation itself, the keyring health check and verify-keyring.
/// </summary>
public sealed class KeyUsageSurvey
{
    private const int MfaPage = 500;

    private readonly IServiceScopeFactory _scopes;
    private readonly MasterKeyProvider _master;
    private readonly PlatformCrypto _crypto;

    public KeyUsageSurvey(IServiceScopeFactory scopes, MasterKeyProvider master, PlatformCrypto crypto)
    {
        _scopes = scopes;
        _master = master;
        _crypto = crypto;
    }

    public async Task<KeyUsageReport> SurveyAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("master key usage survey");
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var counts = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal)
        {
            [KeyArtifacts.ClientDataKey] = (await db.ClientKeys.AsNoTracking().Where(k => k.Status != ClientKeyStatus.Destroyed)
                .GroupBy(k => k.MasterKeyId).Select(g => new { Id = g.Key, N = g.LongCount() }).ToListAsync(cancellationToken))
                .ToDictionary(x => x.Id, x => x.N, StringComparer.Ordinal),
            [KeyArtifacts.LedgerCheckpoint] = (await db.LedgerCheckpoints.AsNoTracking()
                .Where(c => !db.LedgerCheckpointSupersessions.Any(s => s.CheckpointId == c.Id))
                .GroupBy(c => c.KeyId).Select(g => new { Id = g.Key, N = g.LongCount() }).ToListAsync(cancellationToken))
                .ToDictionary(x => x.Id, x => x.N, StringComparer.Ordinal),
        };

        var mfa = new Dictionary<string, long>(StringComparer.Ordinal);
        Guid? after = null;
        while (true)
        {
            var query = db.UserMfa.AsNoTracking().AsQueryable();
            if (after is { } last)
            {
                query = query.Where(m => m.Id.CompareTo(last) > 0);
            }

            var page = await query.OrderBy(m => m.Id).Take(MfaPage).Select(m => new { m.Id, m.SecretEnc }).ToListAsync(cancellationToken);
            foreach (var row in page)
            {
                var id = _crypto.KeyIdOf(row.SecretEnc);
                mfa[id] = mfa.GetValueOrDefault(id) + 1;
            }

            if (page.Count < MfaPage)
            {
                break;
            }

            after = page[^1].Id;
        }

        counts[KeyArtifacts.MfaSecret] = mfa;
        return new KeyUsageReport(_master.CurrentKeyId, _master.Keys.Select(k => k.Id).ToList(), counts);
    }
}
