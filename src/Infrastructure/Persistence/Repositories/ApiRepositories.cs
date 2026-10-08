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

    public async Task<IReadOnlyList<ApiKey>> ListActiveForClientAsync(Guid clientId, CancellationToken cancellationToken) =>
        await _db.ApiKeys.Where(k => k.ClientId == clientId && k.Status == ApiKeyStatus.Active).ToListAsync(cancellationToken);

    public Task<ApiKey?> GetForClientAsync(Guid clientId, Guid keyId, CancellationToken cancellationToken) =>
        _db.ApiKeys.FirstOrDefaultAsync(k => k.ClientId == clientId && k.Id == keyId, cancellationToken);

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

internal sealed class WebhookRepository : IWebhookRepository
{
    private readonly AppDbContext _db;

    public WebhookRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<WebhookEndpoint?> GetEndpointAsync(Guid id, CancellationToken cancellationToken) => _db.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<IReadOnlyList<WebhookEndpoint>> ListEndpointsAsync(CancellationToken cancellationToken) =>
        await _db.WebhookEndpoints.AsNoTracking().OrderBy(e => e.CreatedAt).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WebhookEndpoint>> ListActiveForEventAsync(string eventType, CancellationToken cancellationToken)
    {
        var active = await _db.WebhookEndpoints.AsNoTracking().Where(e => e.Status == WebhookStatus.Active).ToListAsync(cancellationToken);
        return active.Where(e => e.Subscribes(eventType)).ToList();
    }

    public Task<int> CountEndpointsAsync(CancellationToken cancellationToken) => _db.WebhookEndpoints.CountAsync(cancellationToken);

    public Task<WebhookDelivery?> GetDeliveryAsync(long id, CancellationToken cancellationToken) => _db.WebhookDeliveries.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<WebhookDelivery> Items, int Total)> ListDeliveriesAsync(Guid endpointId, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _db.WebhookDeliveries.AsNoTracking().Where(d => d.EndpointId == endpointId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }

    public void Add(WebhookEndpoint endpoint) => _db.WebhookEndpoints.Add(endpoint);

    public void Add(WebhookDelivery delivery) => _db.WebhookDeliveries.Add(delivery);

    public void Remove(WebhookEndpoint endpoint) => _db.WebhookEndpoints.Remove(endpoint);

    public void SetExpectedVersion(WebhookEndpoint endpoint, byte[] rowVersion) => _db.Entry(endpoint).Property(e => e.RowVersion).OriginalValue = rowVersion;
}
