using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Infrastructure.Persistence.Repositories;

internal sealed class LicenseAdjustmentRepository : ILicenseAdjustmentRepository
{
    private readonly AppDbContext _db;

    public LicenseAdjustmentRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<LicenseAdjustmentRequest?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _db.LicenseAdjustmentRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<LicenseAdjustmentRequest> Items, int Total)> ListAsync(
        AdjustmentStatus? status, Guid? licenseId, DateTime now, int skip, int take, CancellationToken cancellationToken)
    {
        var query = _db.LicenseAdjustmentRequests.AsNoTracking().AsQueryable();
        if (licenseId is { } license)
        {
            query = query.Where(r => r.LicenseId == license);
        }

        if (status is { } wanted)
        {
            // Pending means "still open"; Expired also covers pending rows whose deadline has passed but nobody has rewritten yet.
            query = wanted switch
            {
                AdjustmentStatus.Pending => query.Where(r => r.Status == AdjustmentStatus.Pending && r.ExpiresAt > now),
                AdjustmentStatus.Expired => query.Where(r => r.Status == AdjustmentStatus.Expired || (r.Status == AdjustmentStatus.Pending && r.ExpiresAt <= now)),
                _ => query.Where(r => r.Status == wanted),
            };
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(r => r.RequestedAt).ThenByDescending(r => r.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }

    public void Add(LicenseAdjustmentRequest request) => _db.LicenseAdjustmentRequests.Add(request);
}
