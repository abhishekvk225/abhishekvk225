using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Auditing;
using NexaVerify.Domain.Identity;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Infrastructure.Persistence.Queries;

internal sealed class ClientQueries : IClientQueries
{
    private readonly AppDbContext _db;

    public ClientQueries(AppDbContext db)
    {
        _db = db;
    }

    public async Task<(IReadOnlyList<ClientListRow> Items, int Total)> ListClientsAsync(
        string? search, ClientStatus? status, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _db.Clients.AsNoTracking().Where(c => !c.IsSystem);
        if (status is { } s)
        {
            query = query.Where(c => c.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + EscapeLike(search) + "%";
            query = query.Where(c => EF.Functions.Like(c.Name, pattern, "\\") || EF.Functions.Like(c.Code, pattern, "\\") || EF.Functions.Like(c.ContactEmail, pattern, "\\"));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(c => c.Name).Skip(skip).Take(take)
            .Select(c => new { Client = c, UserCount = _db.Users.Count(u => u.ClientId == c.Id) })
            .ToListAsync(cancellationToken);
        return (rows.Select(r => new ClientListRow(r.Client, r.UserCount)).ToList(), total);
    }

    public Task<int> CountActiveUsersAsync(Guid clientId, CancellationToken cancellationToken) =>
        _db.Users.CountAsync(u => u.ClientId == clientId && !u.IsPlatformUser && u.Status == UserStatus.Active && u.IsActive, cancellationToken);

    public async Task<(IReadOnlyList<ClientUserRow> Items, int Total)> ListUsersAsync(
        Guid clientId, string? search, int skip, int take, CancellationToken cancellationToken)
    {
        var users = _db.Users.AsNoTracking().Where(u => u.ClientId == clientId && !u.IsPlatformUser);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + EscapeLike(search) + "%";
            users = users.Where(u => EF.Functions.Like(u.Email, pattern, "\\") || EF.Functions.Like(u.FullName, pattern, "\\"));
        }

        var total = await users.CountAsync(cancellationToken);
        var rows = await ProjectUsers(users.OrderBy(u => u.Email).Skip(skip).Take(take)).ToListAsync(cancellationToken);
        return (rows.Select(r => new ClientUserRow(r.User, r.Membership, r.Role ?? string.Empty)).ToList(), total);
    }

    public async Task<ClientUserRow?> GetUserAsync(Guid clientId, Guid userId, CancellationToken cancellationToken)
    {
        // tracked: callers modify the returned entities
        var row = await ProjectUsers(_db.Users.Where(u => u.ClientId == clientId && !u.IsPlatformUser && u.Id == userId)).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new ClientUserRow(row.User, row.Membership, row.Role ?? string.Empty);
    }

    public async Task<HashSet<Guid>> GetUserIdsAsync(Guid clientId, IReadOnlyCollection<Guid> candidateIds, CancellationToken cancellationToken) =>
        (await _db.Users.AsNoTracking()
            .Where(u => u.ClientId == clientId && !u.IsPlatformUser && candidateIds.Contains(u.Id))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken)).ToHashSet();

    public Task<int> CountActiveUsersWithRoleAsync(Guid clientId, string roleName, CancellationToken cancellationToken) =>
        (from u in _db.Users
         where u.ClientId == clientId && !u.IsPlatformUser && u.Status == UserStatus.Active && u.IsActive
         join ur in _db.UserRoles on u.Id equals ur.UserId
         join r in _db.Roles on ur.RoleId equals r.Id
         where r.Name == roleName
         select u.Id).Distinct().CountAsync(cancellationToken);

    public async Task<(IReadOnlyList<AuditLog> Items, int Total)> ListAuditAsync(
        Guid clientId, DateTime? from, DateTime? to, string? action, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _db.AuditLogs.AsNoTracking().Where(a => a.ClientId == clientId);
        if (from is { } f)
        {
            query = query.Where(a => a.OccurredAt >= f);
        }

        if (to is { } t)
        {
            query = query.Where(a => a.OccurredAt <= t);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(a => a.Action == action);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(a => a.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<(IReadOnlyList<LoginHistory> Items, int Total)> ListLoginsAsync(
        Guid clientId, DateTime? from, DateTime? to, string? outcome, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _db.LoginHistory.AsNoTracking().Where(h => h.ClientId == clientId);
        if (from is { } f)
        {
            query = query.Where(h => h.OccurredAt >= f);
        }

        if (to is { } t)
        {
            query = query.Where(h => h.OccurredAt <= t);
        }

        if (!string.IsNullOrWhiteSpace(outcome) && Enum.TryParse<LoginOutcome>(outcome, ignoreCase: true, out var parsed))
        {
            query = query.Where(h => h.Outcome == parsed);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(h => h.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<RefreshToken>> GetActiveRefreshTokensAsync(Guid clientId, CancellationToken cancellationToken) =>
        await _db.RefreshTokens.Where(t => t.ClientId == clientId && t.RevokedAt == null).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RefreshToken>> GetActiveRefreshTokensForUserAsync(Guid clientId, Guid userId, CancellationToken cancellationToken) =>
        await _db.RefreshTokens.Where(t => t.ClientId == clientId && t.UserId == userId && t.RevokedAt == null).ToListAsync(cancellationToken);

    private IQueryable<UserProjection> ProjectUsers(IQueryable<User> users) =>
        users.Select(u => new UserProjection(
            u,
            _db.ClientUsers.FirstOrDefault(m => m.UserId == u.Id),
            (from ur in _db.UserRoles join r in _db.Roles on ur.RoleId equals r.Id where ur.UserId == u.Id orderby r.Name select r.Name).FirstOrDefault()));

    private static string EscapeLike(string value) =>
        value.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal);

    private sealed record UserProjection(User User, ClientUser? Membership, string? Role);
}
