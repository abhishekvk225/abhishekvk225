using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Platform;

/// <summary>
/// Hands due emails to dispatchers exactly once across all nodes. One statement selects the due rows with <c>UPDLOCK, READPAST</c> (skipping
/// rows another node holds), marks them <c>Sending</c> under a lease and counts the attempt, so a dispatcher that crashes mid-send does not
/// lose the mail (the lease expires and another node takes it) and cannot retry it forever (the attempt was already counted).
/// Raw SQL is confined to this folder by the architecture tests.
/// </summary>
public sealed class EmailStore
{
    private readonly AppDbContext _db;

    public EmailStore(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Claims up to <paramref name="batch"/> due messages, at most <paramref name="perDomain"/> per recipient domain (oldest first), under
    /// <paramref name="leaseToken"/> until <c>now + lease</c>. Messages whose previous lease expired (a crashed node) are due again.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ClaimDueAsync(int batch, int perDomain, DateTime now, TimeSpan lease, Guid leaseToken, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE e SET Status = 'Sending', LeaseToken = @token, LeaseUntil = @leaseUntil, Attempts = e.Attempts + 1, LastAttemptAt = @now
            OUTPUT inserted.Id
            FROM tenancy.OutboundEmails AS e WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE ((e.Status = 'Pending' AND e.NextAttemptAt <= @now) OR (e.Status = 'Sending' AND e.LeaseUntil <= @now))
              AND e.Id IN (
                  SELECT TOP (@batch) c.Id
                  FROM (SELECT Id, NextAttemptAt,
                               ROW_NUMBER() OVER (PARTITION BY ToDomain ORDER BY NextAttemptAt, Id) AS Position
                        FROM tenancy.OutboundEmails WITH (READPAST)
                        WHERE (Status = 'Pending' AND NextAttemptAt <= @now) OR (Status = 'Sending' AND LeaseUntil <= @now)) AS c
                  WHERE c.Position <= @perDomain
                  ORDER BY c.NextAttemptAt, c.Id)
            """;

        return await QueryIdsAsync(
            sql,
            [
                new SqlParameter("@batch", SqlDbType.Int) { Value = batch },
                new SqlParameter("@perDomain", SqlDbType.Int) { Value = perDomain },
                new SqlParameter("@now", SqlDbType.DateTime2) { Value = now, Scale = 3 },
                new SqlParameter("@leaseUntil", SqlDbType.DateTime2) { Value = now + lease, Scale = 3 },
                new SqlParameter("@token", SqlDbType.UniqueIdentifier) { Value = leaseToken },
            ],
            cancellationToken);
    }

    /// <summary>How many delivery attempts were started since <paramref name="since"/> by any node: the platform-wide send rate.</summary>
    public async Task<int> CountAttemptsSinceAsync(DateTime since, CancellationToken cancellationToken)
    {
        await _db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = _db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT COUNT_BIG(*) FROM tenancy.OutboundEmails WHERE LastAttemptAt >= @since";
            command.Parameters.Add(new SqlParameter("@since", SqlDbType.DateTime2) { Value = since, Scale = 3 });
            return (int)Math.Min(int.MaxValue, Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Gives back every message still leased under <paramref name="leaseToken"/> without counting the attempt (graceful shutdown).</summary>
    public async Task<int> ReleaseHeldAsync(Guid leaseToken, DateTime notBefore, CancellationToken cancellationToken)
    {
        await _db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = _db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                UPDATE tenancy.OutboundEmails
                SET Status = 'Pending', LeaseToken = NULL, LeaseUntil = NULL, NextAttemptAt = @notBefore,
                    Attempts = CASE WHEN Attempts > 0 THEN Attempts - 1 ELSE 0 END
                WHERE LeaseToken = @token AND Status = 'Sending'
                """;
            command.Parameters.Add(new SqlParameter("@token", SqlDbType.UniqueIdentifier) { Value = leaseToken });
            command.Parameters.Add(new SqlParameter("@notBefore", SqlDbType.DateTime2) { Value = notBefore, Scale = 3 });
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    private async Task<IReadOnlyList<Guid>> QueryIdsAsync(string sql, SqlParameter[] parameters, CancellationToken cancellationToken)
    {
        await _db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = _db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(parameter);
            }

            var ids = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetGuid(0));
            }

            return ids;
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }
}
