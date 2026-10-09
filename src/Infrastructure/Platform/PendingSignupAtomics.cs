using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Public;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Platform;

/// <summary>
/// The conditional UPDATE that decides which concurrent verification creates the account: it succeeds only while the sign-up is
/// unconsumed, unexpired and the token hash matches, so the emailed link works exactly once. Runs inside the caller's transaction (if
/// the account creation later fails the claim rolls back with it). The stored password hash is erased in the same statement.
/// </summary>
public sealed class PendingSignupAtomics : IPendingSignupAtomics
{
    private readonly AppDbContext _db;

    public PendingSignupAtomics(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> TryClaimAsync(Guid id, byte[] tokenHash, DateTime now, CancellationToken cancellationToken) =>
        await _db.PendingSignups
            .Where(p => p.Id == id && p.ConsumedAt == null && p.ExpiresAt > now && p.TokenHash == tokenHash)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.ConsumedAt, (DateTime?)now)
                .SetProperty(p => p.PasswordHash, string.Empty), cancellationToken) == 1;
}
