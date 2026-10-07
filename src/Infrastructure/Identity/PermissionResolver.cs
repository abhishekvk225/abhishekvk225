using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NexaVerify.Application.Persistence;

namespace NexaVerify.Infrastructure.Identity;

/// <summary>
/// role → permission keys, cached for a minute and dropped whenever RBAC data changes in this process (other instances
/// converge within the TTL). Roles and permissions are global reference data, so no tenant scope is needed.
/// </summary>
public sealed class PermissionResolver : IPermissionResolver
{
    private const string CacheKey = "rbac:role-permissions";
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly Persistence.AppDbContext _db;
    private readonly IMemoryCache _cache;

    public PermissionResolver(Persistence.AppDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(IEnumerable<string> roleNames, CancellationToken cancellationToken)
    {
        var map = await _cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            var rows = await (
                from r in _db.Roles.AsNoTracking()
                from rp in r.Permissions
                join p in _db.Permissions.AsNoTracking() on rp.PermissionId equals p.Id
                select new { r.NormalizedName, p.Key }).ToListAsync(cancellationToken);
            return rows.GroupBy(x => x.NormalizedName)
                .ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g.Select(x => x.Key).ToHashSet(StringComparer.Ordinal));
        }) ?? new Dictionary<string, IReadOnlySet<string>>();

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roleNames)
        {
            if (map.TryGetValue(role.Trim().ToUpperInvariant(), out var permissions))
            {
                result.UnionWith(permissions);
            }
        }

        return result;
    }

    public void Invalidate() => _cache.Remove(CacheKey);
}
