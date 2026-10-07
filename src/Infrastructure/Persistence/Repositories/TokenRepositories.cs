using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Auditing;
using NexaVerify.Domain.Identity;

namespace NexaVerify.Infrastructure.Persistence.Repositories;

internal sealed class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly AppDbContext _db;

    public RefreshTokenRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<RefreshToken?> FindByHashAsync(byte[] hash, CancellationToken cancellationToken) =>
        _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

    public async Task<IReadOnlyList<RefreshToken>> GetActiveForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await _db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RefreshToken>> GetFamilyAsync(Guid familyId, CancellationToken cancellationToken) =>
        await _db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(cancellationToken);

    public void Add(RefreshToken token) => _db.RefreshTokens.Add(token);
}

internal sealed class PasswordResetTokenRepository : IPasswordResetTokenRepository
{
    private readonly AppDbContext _db;

    public PasswordResetTokenRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<PasswordResetToken?> FindUsableAsync(Guid userId, byte[] hash, DateTime now, CancellationToken cancellationToken) =>
        _db.PasswordResetTokens.FirstOrDefaultAsync(
            t => t.UserId == userId && t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now, cancellationToken);

    public async Task<IReadOnlyList<PasswordResetToken>> GetUnusedForUserAsync(Guid userId, DateTime now, CancellationToken cancellationToken) =>
        await _db.PasswordResetTokens.Where(t => t.UserId == userId && t.UsedAt == null && t.ExpiresAt > now).ToListAsync(cancellationToken);

    public void Add(PasswordResetToken token) => _db.PasswordResetTokens.Add(token);
}

internal sealed class LoginHistoryRepository : ILoginHistoryRepository
{
    private readonly AppDbContext _db;

    public LoginHistoryRepository(AppDbContext db)
    {
        _db = db;
    }

    public void Add(LoginHistory entry) => _db.LoginHistory.Add(entry);
}

internal sealed class AuditLogWriter : IAuditLogWriter
{
    private readonly AppDbContext _db;

    public AuditLogWriter(AppDbContext db)
    {
        _db = db;
    }

    public void Add(AuditLog entry) => _db.AuditLogs.Add(entry);
}
