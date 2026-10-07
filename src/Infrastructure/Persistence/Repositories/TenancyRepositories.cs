using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Infrastructure.Persistence.Repositories;

internal sealed class ClientRepository : IClientRepository
{
    private readonly AppDbContext _db;

    public ClientRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<Client?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Clients.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<bool> CodeExistsAsync(string normalizedCode, CancellationToken cancellationToken) =>
        _db.Clients.AnyAsync(c => c.Code == normalizedCode, cancellationToken);

    public void Add(Client client) => _db.Clients.Add(client);

    public void SetExpectedVersion(Client client, byte[] rowVersion) =>
        _db.Entry(client).Property(c => c.RowVersion).OriginalValue = rowVersion;
}

internal sealed class ClientSettingRepository : IClientSettingRepository
{
    private readonly AppDbContext _db;

    public ClientSettingRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ClientSetting>> GetAllAsync(Guid clientId, CancellationToken cancellationToken) =>
        await _db.ClientSettings.Where(s => s.ClientId == clientId).ToListAsync(cancellationToken);

    public void Add(ClientSetting setting) => _db.ClientSettings.Add(setting);

    public void Remove(ClientSetting setting) => _db.ClientSettings.Remove(setting);
}

internal sealed class ClientMembershipRepository : IClientMembershipRepository
{
    private readonly AppDbContext _db;

    public ClientMembershipRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<ClientUser?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.ClientUsers.FirstOrDefaultAsync(m => m.UserId == userId, cancellationToken);

    public void Add(ClientUser membership) => _db.ClientUsers.Add(membership);
}
