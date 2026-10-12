using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Domain.Verification;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Platform;

// M14 persistence that needs raw SQL or set-based deletes/updates (confined to this folder by the architecture tests).
// Raw statements run on the context's own connection (tenant/platform session context applied on open) and join its transaction.

internal static class VerificationSql
{
    public static async Task<List<T>> QueryAsync<T>(AppDbContext db, string sql, SqlParameter[] parameters, Func<DbDataReader, T> map, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = Create(db, sql, parameters);
            var rows = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(map(reader));
            }

            return rows;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public static async Task<int> ExecuteAsync(AppDbContext db, string sql, SqlParameter[] parameters, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = Create(db, sql, parameters);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public static SqlParameter Time(string name, DateTime value) => new(name, SqlDbType.DateTime2) { Value = value, Scale = 3 };

    public static SqlParameter Int(string name, int value) => new(name, SqlDbType.Int) { Value = value };

    public static SqlParameter Guid(string name, System.Guid value) => new(name, SqlDbType.UniqueIdentifier) { Value = value };

    private static DbCommand Create(AppDbContext db, string sql, SqlParameter[] parameters)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        return command;
    }
}

/// <summary>
/// The verification work queue. Each method is one batch (own transaction unless the caller has one) that changes the requests and appends
/// the matching <c>Events</c> rows; claiming uses <c>UPDLOCK, READPAST</c> so concurrent nodes never take the same row.
/// </summary>
public sealed class VerificationQueue : IVerificationQueue
{
    private const string EventInsert = """
        INSERT INTO verification.Events (ClientId, RequestId, FromStatus, ToStatus, Stage, NoteCode, ActorType, ActorId, CreatedAt)
        """;

    private readonly AppDbContext _db;

    public VerificationQueue(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ClaimedVerification>> ClaimDueAsync(
        int batch, int perClientLimit, DateTime now, TimeSpan lease, Guid leaseToken, CancellationToken cancellationToken)
    {
        // The status/NextAttemptAt literals match the filtered claim index. Position <= perClient keeps one tenant from taking the whole batch.
        var sql = $"""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            DECLARE @claimed TABLE (Id uniqueidentifier, ClientId uniqueidentifier, Type nvarchar(30), Attempts int);
            UPDATE r SET Status = 'Processing', Attempts = r.Attempts + 1, LeaseToken = @token, LeaseUntil = @leaseUntil,
                         NextAttemptAt = NULL, StartedAt = COALESCE(r.StartedAt, @now), UpdatedAt = @now
            OUTPUT inserted.Id, inserted.ClientId, inserted.Type, inserted.Attempts INTO @claimed
            FROM verification.Requests AS r WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE r.Status = 'Queued' AND r.NextAttemptAt <= @now
              AND r.Id IN (
                  SELECT TOP (@batch) c.Id
                  FROM (SELECT Id, NextAttemptAt,
                               ROW_NUMBER() OVER (PARTITION BY ClientId ORDER BY NextAttemptAt, Id) AS Position
                        FROM verification.Requests WITH (READPAST)
                        WHERE Status = 'Queued' AND NextAttemptAt <= @now) AS c
                  WHERE c.Position <= @perClient
                  ORDER BY c.NextAttemptAt, c.Id);
            {EventInsert}
            SELECT ClientId, Id, 'Queued', 'Processing', 'Claim', NULL, 'System', NULL, @now FROM @claimed;
            SELECT Id, ClientId, Type, Attempts FROM @claimed;
            COMMIT;
            """;
        var leaseUntil = now + lease;
        return await VerificationSql.QueryAsync(
            _db,
            sql,
            [
                VerificationSql.Int("@batch", batch), VerificationSql.Int("@perClient", perClientLimit), VerificationSql.Time("@now", now),
                VerificationSql.Time("@leaseUntil", leaseUntil), VerificationSql.Guid("@token", leaseToken),
            ],
            r => new ClaimedVerification(r.GetGuid(0), r.GetGuid(1), Enum.Parse<VerificationType>(r.GetString(2)), r.GetInt32(3), leaseToken, leaseUntil),
            cancellationToken);
    }

    public async Task<bool> ExtendLeaseAsync(Guid requestId, Guid leaseToken, DateTime leaseUntil, DateTime now, CancellationToken cancellationToken) =>
        await VerificationSql.ExecuteAsync(
            _db,
            "UPDATE verification.Requests SET LeaseUntil = @until, UpdatedAt = @now WHERE Id = @id AND LeaseToken = @token AND Status = 'Processing'",
            [VerificationSql.Time("@until", leaseUntil), VerificationSql.Time("@now", now), VerificationSql.Guid("@id", requestId), VerificationSql.Guid("@token", leaseToken)],
            cancellationToken) > 0;

    public async Task<LeaseSweepResult> RequeueExpiredLeasesAsync(DateTime now, int maxAttempts, int take, CancellationToken cancellationToken)
    {
        var sql = $"""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            DECLARE @t TABLE (Id uniqueidentifier, ClientId uniqueidentifier, ToStatus nvarchar(30));
            UPDATE r SET Status = CASE WHEN r.Attempts >= @max THEN 'Failed' ELSE 'Queued' END,
                         ErrorCode = CASE WHEN r.Attempts >= @max THEN 'PROCESSING_TIMEOUT' ELSE r.ErrorCode END,
                         CompletedAt = CASE WHEN r.Attempts >= @max THEN @now ELSE r.CompletedAt END,
                         NextAttemptAt = CASE WHEN r.Attempts >= @max THEN NULL ELSE @now END,
                         LeaseToken = NULL, LeaseUntil = NULL, UpdatedAt = @now
            OUTPUT inserted.Id, inserted.ClientId, inserted.Status INTO @t
            FROM verification.Requests AS r WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE r.Status = 'Processing' AND r.LeaseUntil <= @now
              AND r.Id IN (SELECT TOP (@take) Id FROM verification.Requests WITH (READPAST) WHERE Status = 'Processing' AND LeaseUntil <= @now ORDER BY LeaseUntil);
            {EventInsert}
            SELECT ClientId, Id, 'Processing', ToStatus, NULL, CASE WHEN ToStatus = 'Failed' THEN 'PROCESSING_TIMEOUT' ELSE 'LEASE_EXPIRED' END, 'System', NULL, @now FROM @t;
            SELECT COALESCE(SUM(CASE WHEN ToStatus = 'Queued' THEN 1 ELSE 0 END), 0), COALESCE(SUM(CASE WHEN ToStatus = 'Failed' THEN 1 ELSE 0 END), 0) FROM @t;
            COMMIT;
            """;
        var rows = await VerificationSql.QueryAsync(
            _db, sql, [VerificationSql.Time("@now", now), VerificationSql.Int("@max", maxAttempts), VerificationSql.Int("@take", take)],
            r => new LeaseSweepResult(r.GetInt32(0), r.GetInt32(1)), cancellationToken);
        return rows.Single();
    }

    public async Task<int> ReleaseHeldAsync(Guid leaseToken, DateTime notBefore, DateTime now, CancellationToken cancellationToken)
    {
        var sql = $"""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            DECLARE @t TABLE (Id uniqueidentifier, ClientId uniqueidentifier);
            UPDATE verification.Requests SET Status = 'Queued', LeaseToken = NULL, LeaseUntil = NULL, NextAttemptAt = @notBefore, UpdatedAt = @now,
                   Attempts = CASE WHEN Attempts > 0 THEN Attempts - 1 ELSE 0 END
            OUTPUT inserted.Id, inserted.ClientId INTO @t
            WHERE LeaseToken = @token AND Status = 'Processing';
            {EventInsert}
            SELECT ClientId, Id, 'Processing', 'Queued', NULL, 'SHUTDOWN', 'System', NULL, @now FROM @t;
            SELECT COUNT(*) FROM @t;
            COMMIT;
            """;
        var rows = await VerificationSql.QueryAsync(
            _db, sql, [VerificationSql.Guid("@token", leaseToken), VerificationSql.Time("@notBefore", notBefore), VerificationSql.Time("@now", now)],
            r => r.GetInt32(0), cancellationToken);
        return rows.Single();
    }

    public async Task<int> ExpireStaleQueuedAsync(DateTime queuedBefore, DateTime now, int take, CancellationToken cancellationToken)
    {
        var sql = $"""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            DECLARE @t TABLE (Id uniqueidentifier, ClientId uniqueidentifier);
            UPDATE r SET Status = 'Expired', ErrorCode = 'EXPIRED', CompletedAt = @now, NextAttemptAt = NULL, UpdatedAt = @now
            OUTPUT inserted.Id, inserted.ClientId INTO @t
            FROM verification.Requests AS r WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE r.Status = 'Queued' AND r.ExtractionState = 'NotStarted' AND r.RulesState = 'NotStarted'
              AND r.Id IN (SELECT TOP (@take) Id FROM verification.Requests WITH (READPAST)
                           WHERE Status = 'Queued' AND ExtractionState = 'NotStarted' AND RulesState = 'NotStarted' AND CreatedAt < @before ORDER BY CreatedAt);
            {EventInsert}
            SELECT ClientId, Id, 'Queued', 'Expired', NULL, 'EXPIRED', 'System', NULL, @now FROM @t;
            SELECT COUNT(*) FROM @t;
            COMMIT;
            """;
        var rows = await VerificationSql.QueryAsync(
            _db, sql, [VerificationSql.Time("@before", queuedBefore), VerificationSql.Time("@now", now), VerificationSql.Int("@take", take)],
            r => r.GetInt32(0), cancellationToken);
        return rows.Single();
    }
}

/// <summary>Result and check writes that must delete first (the entities are immutable, a re-run replaces them).</summary>
internal sealed class VerificationResultStore : IVerificationResultStore
{
    private readonly AppDbContext _db;

    public VerificationResultStore(AppDbContext db)
    {
        _db = db;
    }

    public Task<VerificationResult?> GetResultAsync(Guid requestId, CancellationToken cancellationToken) =>
        _db.VerificationResults.AsNoTracking().FirstOrDefaultAsync(r => r.RequestId == requestId, cancellationToken);

    public async Task ReplaceResultAsync(VerificationResult result, CancellationToken cancellationToken)
    {
        await _db.VerificationResults.Where(r => r.RequestId == result.RequestId).ExecuteDeleteAsync(cancellationToken);
        _db.VerificationResults.Add(result);
    }

    public async Task<IReadOnlyList<VerificationCheck>> ListChecksAsync(Guid requestId, CancellationToken cancellationToken) =>
        await _db.VerificationChecks.AsNoTracking().Where(c => c.RequestId == requestId).OrderBy(c => c.Ordinal).ThenBy(c => c.Id).ToListAsync(cancellationToken);

    public async Task ReplaceChecksAsync(Guid requestId, IReadOnlyCollection<CheckStage> stages, IEnumerable<VerificationCheck> checks, CancellationToken cancellationToken)
    {
        var stageList = stages.ToArray();
        await _db.VerificationChecks.Where(c => c.RequestId == requestId && stageList.Contains(c.Stage)).ExecuteDeleteAsync(cancellationToken);
        _db.VerificationChecks.AddRange(checks);
    }

    public Task<int> CountByIdentifierHashAsync(byte[] identifierHash, Guid? excludingRequestId, CancellationToken cancellationToken) =>
        _db.VerificationResults.CountAsync(r => r.IdentifierHash == identifierHash && (excludingRequestId == null || r.RequestId != excludingRequestId), cancellationToken);
}

internal sealed class VerificationDownloadGrantStore : IVerificationDownloadGrants
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public VerificationDownloadGrantStore(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public void Add(VerificationDownloadGrant grant) => _db.VerificationDownloadGrants.Add(grant);

    public async Task<GrantConsumeResult> ConsumeAsync(byte[] tokenHash, Guid principalId, DateTime now, CancellationToken cancellationToken)
    {
        if (_tenant.ClientId is not { } clientId)
        {
            return new GrantConsumeResult(GrantConsumeStatus.NotFound, null, null, null);
        }

        SqlParameter Hash() => new("@hash", SqlDbType.Binary, 32) { Value = tokenHash };

        var consumed = await VerificationSql.QueryAsync(
            _db,
            """
            UPDATE g SET UsedAt = @now
            OUTPUT inserted.RequestId, inserted.Kind, inserted.Format
            FROM verification.DownloadGrants AS g
            WHERE g.TokenHash = @hash AND g.ClientId = @client AND g.PrincipalId = @principal AND g.UsedAt IS NULL AND g.ExpiresAt > @now
            """,
            [Hash(), VerificationSql.Guid("@client", clientId), VerificationSql.Guid("@principal", principalId), VerificationSql.Time("@now", now)],
            r => new GrantConsumeResult(GrantConsumeStatus.Consumed, r.GetGuid(0), Enum.Parse<DownloadKind>(r.GetString(1)), r.IsDBNull(2) ? null : r.GetString(2)),
            cancellationToken);
        if (consumed.Count == 1)
        {
            return consumed[0];
        }

        var existing = await VerificationSql.QueryAsync(
            _db, "SELECT PrincipalId FROM verification.DownloadGrants WHERE TokenHash = @hash AND ClientId = @client",
            [Hash(), VerificationSql.Guid("@client", clientId)], r => r.GetGuid(0), cancellationToken);
        if (existing.Count == 0)
        {
            return new GrantConsumeResult(GrantConsumeStatus.NotFound, null, null, null);
        }

        return new GrantConsumeResult(existing[0] == principalId ? GrantConsumeStatus.ExpiredOrUsed : GrantConsumeStatus.NotOwner, null, null, null);
    }
}

internal sealed class VerificationRetention : IVerificationRetention
{
    private readonly AppDbContext _db;

    public VerificationRetention(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<DueVerification>> ListRequestsDueAsync(DateTime now, int take, CancellationToken cancellationToken) =>
        await _db.VerificationRequests.AsNoTracking()
            .Where(r => r.PurgedAt == null && r.ExpiresAt != null && r.ExpiresAt <= now)
            .OrderBy(r => r.ExpiresAt).Take(Math.Clamp(take, 1, 1000))
            .Select(r => new DueVerification(r.Id, r.ClientId)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListDocumentsDueAsync(DateTime now, int take, CancellationToken cancellationToken) =>
        await _db.VerificationDocuments.AsNoTracking().Where(d => d.PurgedAt == null && d.PurgeAfter <= now)
            .OrderBy(d => d.PurgeAfter).Take(Math.Clamp(take, 1, 1000)).Select(d => d.RequestId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListResultsDueAsync(DateTime now, int take, CancellationToken cancellationToken) =>
        await _db.VerificationResults.AsNoTracking().Where(r => r.RetainUntil <= now)
            .OrderBy(r => r.RetainUntil).Take(Math.Clamp(take, 1, 1000)).Select(r => r.RequestId).ToListAsync(cancellationToken);

    public async Task<bool> PurgeDocumentAsync(Guid requestId, DateTime now, CancellationToken cancellationToken)
    {
        var blobs = await _db.VerificationDocumentBlobs
            .Where(b => _db.VerificationDocuments.Any(d => d.Id == b.DocumentId && d.RequestId == requestId)).ExecuteDeleteAsync(cancellationToken);
        var docs = await _db.VerificationDocuments.Where(d => d.RequestId == requestId && d.PurgedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.PurgedAt, now), cancellationToken);
        return blobs + docs > 0;
    }

    public async Task<bool> PurgeResultAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var results = await _db.VerificationResults.Where(r => r.RequestId == requestId).ExecuteDeleteAsync(cancellationToken);
        var checks = await _db.VerificationChecks.Where(c => c.RequestId == requestId).ExecuteDeleteAsync(cancellationToken);
        return results + checks > 0;
    }

    public Task<bool> EraseAsync(Guid requestId, DateTime now, CancellationToken cancellationToken) =>
        _db.ExecuteInTransactionAsync(
            async token =>
            {
                var document = await PurgeDocumentAsync(requestId, now, token);
                var result = await PurgeResultAsync(requestId, token);
                var request = await _db.VerificationRequests.Where(r => r.Id == requestId && r.PurgedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.PurgedAt, now).SetProperty(r => r.UpdatedAt, now), token);
                return document || result || request > 0;
            },
            cancellationToken);

    public async Task<int> PurgeExpiredGrantsAsync(DateTime expiredBefore, int take, CancellationToken cancellationToken)
    {
        var ids = await _db.VerificationDownloadGrants.AsNoTracking().Where(g => g.ExpiresAt < expiredBefore)
            .OrderBy(g => g.ExpiresAt).Take(Math.Clamp(take, 1, 5000)).Select(g => g.Id).ToListAsync(cancellationToken);
        return ids.Count == 0 ? 0 : await _db.VerificationDownloadGrants.Where(g => ids.Contains(g.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> BlankIpAddressesAsync(DateTime createdBefore, int take, CancellationToken cancellationToken)
    {
        var ids = await _db.VerificationRequests.AsNoTracking().Where(r => r.IpAddress != null && r.CreatedAt < createdBefore)
            .OrderBy(r => r.CreatedAt).Take(Math.Clamp(take, 1, 5000)).Select(r => r.Id).ToListAsync(cancellationToken);
        return ids.Count == 0 ? 0 : await _db.VerificationRequests.Where(r => ids.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.IpAddress, (string?)null), cancellationToken);
    }
}
