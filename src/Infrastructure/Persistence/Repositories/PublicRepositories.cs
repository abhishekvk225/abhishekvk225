using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Public;
using NexaVerify.Domain.Identity;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Infrastructure.Persistence.Repositories;

internal sealed class PendingSignupRepository : IPendingSignupRepository
{
    private readonly AppDbContext _db;

    public PendingSignupRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<PendingSignup?> FindLiveAsync(string normalizedEmail, DateTime now, CancellationToken cancellationToken) =>
        _db.PendingSignups.AsNoTracking()
            .FirstOrDefaultAsync(p => p.NormalizedEmail == normalizedEmail && p.ConsumedAt == null && p.ExpiresAt > now, cancellationToken);

    public Task<PendingSignup?> FindLiveForUpdateAsync(string normalizedEmail, DateTime now, CancellationToken cancellationToken) =>
        _db.PendingSignups.FirstOrDefaultAsync(p => p.NormalizedEmail == normalizedEmail && p.ConsumedAt == null && p.ExpiresAt > now, cancellationToken);

    public async Task<IReadOnlyList<PendingSignup>> GetUnconsumedAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        await _db.PendingSignups.Where(p => p.NormalizedEmail == normalizedEmail && p.ConsumedAt == null).ToListAsync(cancellationToken);

    public void Add(PendingSignup signup) => _db.PendingSignups.Add(signup);

    public void Remove(PendingSignup signup) => _db.PendingSignups.Remove(signup);
}

internal sealed class ContactRequestRepository : IContactRequestRepository
{
    private readonly AppDbContext _db;

    public ContactRequestRepository(AppDbContext db)
    {
        _db = db;
    }

    public void Add(ContactRequest request) => _db.ContactRequests.Add(request);

    public async Task<(IReadOnlyList<ContactRequest> Items, int Total)> ListAsync(int skip, int take, CancellationToken cancellationToken)
    {
        var query = _db.ContactRequests.AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }
}
