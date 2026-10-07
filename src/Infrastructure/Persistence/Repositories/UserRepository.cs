using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Identity;

namespace NexaVerify.Infrastructure.Persistence.Repositories;

internal sealed class UserRepository : IUserRepository
{
    private readonly AppDbContext _db;

    public UserRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        _db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

    public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        _db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

    public async Task<(IReadOnlyList<User> Items, int Total)> ListAsync(bool platformUsers, string? search, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _db.Users.AsNoTracking().Where(u => u.IsPlatformUser == platformUsers);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + EscapeLike(search.Trim()) + "%";
            query = query.Where(u => EF.Functions.Like(u.Email, pattern, "\\") || EF.Functions.Like(u.FullName, pattern, "\\"));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(u => u.Email).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<string>> GetRoleNamesAsync(Guid userId, CancellationToken cancellationToken) =>
        await (from ur in _db.UserRoles.AsNoTracking()
               where ur.UserId == userId
               join r in _db.Roles.AsNoTracking() on ur.RoleId equals r.Id
               orderby r.Name
               select r.Name).ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetRoleNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<string>>();
        }

        var rows = await (from ur in _db.UserRoles.AsNoTracking()
                          where userIds.Contains(ur.UserId)
                          join r in _db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                          select new { ur.UserId, r.Name }).ToListAsync(cancellationToken);
        return rows.GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name).OrderBy(n => n, StringComparer.Ordinal).ToList());
    }

    public async Task SetRolesAsync(User user, IReadOnlyCollection<Role> roles, CancellationToken cancellationToken)
    {
        var wanted = roles.Select(r => r.Id).ToHashSet();
        var existing = await _db.UserRoles.Where(ur => ur.UserId == user.Id).ToListAsync(cancellationToken);
        _db.UserRoles.RemoveRange(existing.Where(ur => !wanted.Contains(ur.RoleId)));
        var have = existing.Select(ur => ur.RoleId).ToHashSet();
        foreach (var role in roles.Where(r => !have.Contains(r.Id)))
        {
            _db.UserRoles.Add(new UserRole { ClientId = user.ClientId, UserId = user.Id, RoleId = role.Id });
        }
    }

    public void Add(User user) => _db.Users.Add(user);

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal);
}
