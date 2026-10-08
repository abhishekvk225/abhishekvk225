using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Persistence;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Platform;

/// <summary>
/// Single-statement MFA state changes. Each is a conditional UPDATE whose affected-row count says who won, so concurrent
/// requests (a parallel brute force, a replayed code, a double-submitted recovery code) cannot both succeed. Set-based statements
/// are confined to this folder by the architecture tests.
/// </summary>
public sealed class MfaAtomics : IMfaAtomics
{
    private readonly AppDbContext _db;

    public MfaAtomics(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> TryRegisterAttemptAsync(Guid challengeId, int maxAttempts, DateTime now, CancellationToken cancellationToken) =>
        await _db.MfaChallenges
            .Where(c => c.Id == challengeId && c.ConsumedAt == null && c.ExpiresAt > now && c.Attempts < maxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Attempts, c => c.Attempts + 1), cancellationToken) == 1;

    public async Task<bool> TryConsumeChallengeAsync(Guid challengeId, DateTime now, CancellationToken cancellationToken) =>
        await _db.MfaChallenges
            .Where(c => c.Id == challengeId && c.ConsumedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), cancellationToken) == 1;

    public async Task<bool> TryAdvanceStepAsync(Guid userId, long step, CancellationToken cancellationToken) =>
        await _db.UserMfa
            .Where(m => m.UserId == userId && m.IsConfirmed && (m.LastUsedStep == null || m.LastUsedStep < step))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastUsedStep, (long?)step), cancellationToken) == 1;

    public async Task<bool> TryUseRecoveryCodeAsync(Guid codeId, DateTime now, CancellationToken cancellationToken) =>
        await _db.MfaRecoveryCodes
            .Where(c => c.Id == codeId && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, (DateTime?)now), cancellationToken) == 1;

    public async Task PurgeChallengesAsync(Guid userId, DateTime now, CancellationToken cancellationToken) =>
        await _db.MfaChallenges
            .Where(c => c.UserId == userId && (c.ExpiresAt <= now || c.ConsumedAt != null))
            .ExecuteDeleteAsync(cancellationToken);
}
