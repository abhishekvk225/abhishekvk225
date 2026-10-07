using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Common;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Infrastructure.Tenancy;

/// <summary>
/// Decides whether a client may sign in or call the API right now. The status is cached for 30 s and the cache entry is
/// dropped explicitly whenever a status changes in this process, so suspension applies to the very next request here and
/// within the TTL on other instances.
/// </summary>
public sealed class ClientAccessGuard : IClientAccessGuard
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly Persistence.AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ITenantScope _scope;

    public ClientAccessGuard(Persistence.AppDbContext db, IMemoryCache cache, ITenantScope scope)
    {
        _db = db;
        _cache = cache;
        _scope = scope;
    }

    public async Task<Error?> CheckAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var status = await _cache.GetOrCreateAsync(Key(clientId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            using var scope = _scope.BeginPlatform("client status check");
            return await _db.Clients.AsNoTracking()
                .Where(c => c.Id == clientId)
                .Select(c => (ClientStatus?)c.Status)
                .FirstOrDefaultAsync(CancellationToken.None);
        });

        return status switch
        {
            ClientStatus.Active => null,
            ClientStatus.Suspended => Error.Forbidden(ErrorCodes.ClientSuspended, "This account is suspended. Please contact support."),
            _ => Error.Forbidden(ErrorCodes.ClientInactive, "This account is not active. Please contact support."),
        };
    }

    public void Invalidate(Guid clientId) => _cache.Remove(Key(clientId));

    private static string Key(Guid clientId) => "client-status:" + clientId.ToString("N");
}
