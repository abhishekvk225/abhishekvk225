using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexaVerify.Application.Persistence;

namespace NexaVerify.Infrastructure.Platform;

/// <summary>
/// Failed-attempt accounting done as ONE atomic UPDATE … OUTPUT per attempt, so concurrent requests cannot lose increments.
/// Lives in the Platform folder because it deliberately uses raw SQL (the architecture test confines raw SQL to approved folders).
/// </summary>
public sealed class LoginThrottle : ILoginThrottle
{
    private readonly Persistence.AppDbContext _db;

    public LoginThrottle(Persistence.AppDbContext db)
    {
        _db = db;
    }

    public async Task<AttemptState> ReserveAttemptAsync(Guid userId, DateTime now, int maxAttempts, TimeSpan lockoutDuration, CancellationToken cancellationToken)
    {
        // new count = (expired lock → 1, else previous + 1). Allowed while count <= max; the attempt that exceeds it locks the account.
        const string sql = """
            UPDATE u
            SET AccessFailedCount = CASE WHEN c.NewCount > @max THEN 0 ELSE c.NewCount END,
                LockoutEnd = CASE WHEN c.NewCount > @max THEN @lockEnd
                                  WHEN u.LockoutEnd IS NOT NULL AND u.LockoutEnd <= @now THEN NULL
                                  ELSE u.LockoutEnd END
            OUTPUT inserted.AccessFailedCount, inserted.LockoutEnd
            FROM iam.Users u
            CROSS APPLY (SELECT CASE WHEN u.LockoutEnd IS NOT NULL AND u.LockoutEnd <= @now THEN 1 ELSE u.AccessFailedCount + 1 END AS NewCount) c
            WHERE u.Id = @id
            """;

        await _db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = _db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
            command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = userId });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            command.Parameters.Add(new SqlParameter("@lockEnd", SqlDbType.DateTime2) { Value = now.Add(lockoutDuration) });
            command.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = maxAttempts });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new AttemptState(true, 0, null); // user vanished: treat as locked
            }

            var attempts = reader.GetInt32(0);
            DateTime? lockoutEnd = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
            return new AttemptState(lockoutEnd > now, attempts, lockoutEnd);
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    public async Task ClearAsync(Guid userId, CancellationToken cancellationToken) =>
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE iam.Users SET AccessFailedCount = 0, LockoutEnd = NULL WHERE Id = {userId} AND (AccessFailedCount <> 0 OR LockoutEnd IS NOT NULL)",
            cancellationToken);
}

/// <summary>Exactly one concurrent caller can claim (revoke-as-rotated) a given refresh token.</summary>
public sealed class RefreshTokenClaimer : IRefreshTokenClaimer
{
    private readonly Persistence.AppDbContext _db;

    public RefreshTokenClaimer(Persistence.AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> TryClaimAsync(Guid tokenId, DateTime now, CancellationToken cancellationToken) =>
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE iam.RefreshTokens SET RevokedAt = {now}, RevokedReason = N'rotated' WHERE Id = {tokenId} AND RevokedAt IS NULL",
            cancellationToken) == 1;
}
