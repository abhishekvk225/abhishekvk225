using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Api;

namespace NexaVerify.Infrastructure.Persistence.Repositories;

internal sealed class ApiKeyRepository : IApiKeyRepository
{
    private readonly AppDbContext _db;

    public ApiKeyRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<ApiKey?> GetAsync(Guid id, CancellationToken cancellationToken) => _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken cancellationToken) =>
        await _db.ApiKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).ToListAsync(cancellationToken);

    public Task<int> CountActiveAsync(DateTime now, CancellationToken cancellationToken) =>
        _db.ApiKeys.CountAsync(k => k.Status == ApiKeyStatus.Active && (k.ExpiresAt == null || k.ExpiresAt > now), cancellationToken);

    public Task<bool> PrefixExistsAsync(string prefix, CancellationToken cancellationToken) => _db.ApiKeys.AnyAsync(k => k.KeyPrefix == prefix, cancellationToken);

    public void Add(ApiKey key) => _db.ApiKeys.Add(key);

    public void SetExpectedVersion(ApiKey key, byte[] rowVersion) => _db.Entry(key).Property(k => k.RowVersion).OriginalValue = rowVersion;
}

internal sealed class ApiLogRepository : IApiLogRepository
{
    private readonly AppDbContext _db;

    public ApiLogRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<(IReadOnlyList<ApiRequestLog> Items, int Total)> ListAsync(
        Guid? apiKeyId, string? statusClass, DateTime? from, DateTime? to, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _db.ApiRequestLogs.AsNoTracking().AsQueryable();
        if (apiKeyId is { } key)
        {
            query = query.Where(l => l.ApiKeyId == key);
        }

        query = statusClass?.ToLowerInvariant() switch
        {
            "success" => query.Where(l => l.StatusCode >= 200 && l.StatusCode < 300),
            "clienterror" => query.Where(l => l.StatusCode >= 400 && l.StatusCode < 500),
            "servererror" => query.Where(l => l.StatusCode >= 500),
            _ => query,
        };

        if (from is { } f)
        {
            query = query.Where(l => l.CreatedAt >= f);
        }

        if (to is { } t)
        {
            query = query.Where(l => l.CreatedAt < t);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }
}
