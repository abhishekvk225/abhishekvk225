using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Licensing;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Platform;

/// <summary>
/// The decision on an adjustment request is ONE conditional UPDATE (<c>WHERE Status = 'Pending' AND ExpiresAt &gt; now</c>), so two
/// approvers racing — or one approver double-clicking — cannot both apply it, and an expired request cannot be approved late.
/// The caller runs it in the same transaction as the ledger write, so a failed adjustment leaves the request pending.
/// </summary>
public sealed class LicenseAdjustmentAtomics : ILicenseAdjustmentAtomics
{
    private readonly AppDbContext _db;

    public LicenseAdjustmentAtomics(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> TryDecideAsync(Guid id, AdjustmentStatus outcome, Guid decidedBy, string? note, DateTime now, CancellationToken cancellationToken) =>
        await _db.LicenseAdjustmentRequests
            .Where(r => r.Id == id && r.Status == AdjustmentStatus.Pending && r.ExpiresAt > now)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, outcome)
                    .SetProperty(r => r.DecidedBy, (Guid?)decidedBy)
                    .SetProperty(r => r.DecidedAt, (DateTime?)now)
                    .SetProperty(r => r.DecisionNote, note),
                cancellationToken) == 1;

    public async Task<bool> TryExpireAsync(Guid id, DateTime now, CancellationToken cancellationToken) =>
        await _db.LicenseAdjustmentRequests
            .Where(r => r.Id == id && r.Status == AdjustmentStatus.Pending && r.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, AdjustmentStatus.Expired).SetProperty(r => r.DecidedAt, (DateTime?)now), cancellationToken) == 1;

    public async Task SetLedgerEntryAsync(Guid id, long ledgerTransactionId, CancellationToken cancellationToken) =>
        await _db.LicenseAdjustmentRequests.Where(r => r.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LedgerTransactionId, (long?)ledgerTransactionId), cancellationToken);
}
