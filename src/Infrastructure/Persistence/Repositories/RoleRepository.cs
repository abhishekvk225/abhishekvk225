using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Identity;

namespace NexaVerify.Infrastructure.Persistence.Repositories;

internal sealed class RoleRepository : IRoleRepository
{
    private readonly AppDbContext _db;

    public RoleRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Role>> ListAsync(CancellationToken cancellationToken) =>
        await _db.Roles.Include(r => r.Permissions).OrderBy(r => r.Name).ToListAsync(cancellationToken);

    public Task<Role?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Role>> GetByNamesAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        var normalized = names.Select(n => n.Trim().ToUpperInvariant()).ToList();
        return await _db.Roles.Include(r => r.Permissions).Where(r => normalized.Contains(r.NormalizedName)).ToListAsync(cancellationToken);
    }

    public Task<bool> NameExistsAsync(string normalizedName, CancellationToken cancellationToken) =>
        _db.Roles.AnyAsync(r => r.NormalizedName == normalizedName, cancellationToken);

    public async Task<IReadOnlyList<Permission>> ListPermissionsAsync(CancellationToken cancellationToken) =>
        await _db.Permissions.AsNoTracking().ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Permission>> GetPermissionsByKeysAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken) =>
        await _db.Permissions.Where(p => keys.Contains(p.Key)).ToListAsync(cancellationToken);

    public void Add(Role role) => _db.Roles.Add(role);
}
