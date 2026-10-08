using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Identity;

namespace NexaVerify.Infrastructure.Persistence.Repositories;

internal sealed class MfaRepository : IMfaRepository
{
    private readonly AppDbContext _db;

    public MfaRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<UserMfa?> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.UserMfa.FirstOrDefaultAsync(m => m.UserId == userId, cancellationToken);

    public void Add(UserMfa mfa) => _db.UserMfa.Add(mfa);

    public void Remove(UserMfa mfa) => _db.UserMfa.Remove(mfa);

    public Task<int> CountUnusedCodesAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.MfaRecoveryCodes.CountAsync(c => c.UserId == userId && c.UsedAt == null, cancellationToken);

    public Task<MfaRecoveryCode?> FindUnusedCodeAsync(Guid userId, byte[] codeHash, CancellationToken cancellationToken) =>
        _db.MfaRecoveryCodes.FirstOrDefaultAsync(c => c.UserId == userId && c.CodeHash == codeHash && c.UsedAt == null, cancellationToken);

    public async Task<IReadOnlyList<MfaRecoveryCode>> GetCodesAsync(Guid userId, CancellationToken cancellationToken) =>
        await _db.MfaRecoveryCodes.Where(c => c.UserId == userId).ToListAsync(cancellationToken);

    public void Add(MfaRecoveryCode code) => _db.MfaRecoveryCodes.Add(code);

    public void Remove(MfaRecoveryCode code) => _db.MfaRecoveryCodes.Remove(code);

    public Task<MfaChallenge?> FindChallengeAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
        _db.MfaChallenges.AsNoTracking().FirstOrDefaultAsync(c => c.TokenHash == tokenHash, cancellationToken);

    public void Add(MfaChallenge challenge) => _db.MfaChallenges.Add(challenge);
}
