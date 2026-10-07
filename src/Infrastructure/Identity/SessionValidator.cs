using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Persistence;

namespace NexaVerify.Infrastructure.Identity;

/// <summary>
/// Called for every authenticated request: is the token's user still active, not locked, on the same security version, and
/// is its client still allowed in? The answer is cached for 30 s so deactivation, password changes and client suspension
/// take effect within that window without a database hit per request.
/// </summary>
public sealed class SessionValidator : ISessionValidator
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly Persistence.AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IClientAccessGuard _clientGuard;
    private readonly ITenantScope _scope;
    private readonly TimeProvider _time;

    public SessionValidator(Persistence.AppDbContext db, IMemoryCache cache, IClientAccessGuard clientGuard, ITenantScope scope, TimeProvider time)
    {
        _db = db;
        _cache = cache;
        _clientGuard = clientGuard;
        _scope = scope;
        _time = time;
    }

    public async Task<bool> IsValidAsync(Guid userId, int securityVersion, CancellationToken cancellationToken)
    {
        var state = await _cache.GetOrCreateAsync(Key(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            // The tenant is not resolved yet while the token is being validated, hence the reasoned system scope.
            using var scope = _scope.BeginPlatform("session validation");
            var user = await _db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.SecurityVersion, u.Status, u.IsActive, u.LockoutEnd, u.ClientId, u.IsPlatformUser })
                .FirstOrDefaultAsync(CancellationToken.None);
            if (user is null)
            {
                return new SessionState(false, 0, Guid.Empty, false);
            }

            var usable = user.Status == Domain.Identity.UserStatus.Active && user.IsActive; // a lockout only blocks password sign-in, never live sessions
            return new SessionState(usable, user.SecurityVersion, user.ClientId, user.IsPlatformUser);
        });

        if (state is not { Usable: true } || state.SecurityVersion != securityVersion)
        {
            return false;
        }

        // The client's status is checked on every request (cheap: the guard caches it and is invalidated explicitly on change).
        return state.IsPlatformUser || await _clientGuard.CheckAsync(state.ClientId, cancellationToken) is null;
    }

    public void Invalidate(Guid userId) => _cache.Remove(Key(userId));

    private static string Key(Guid userId) => "session:" + userId.ToString("N");

    private sealed record SessionState(bool Usable, int SecurityVersion, Guid ClientId, bool IsPlatformUser);
}
