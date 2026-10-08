using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Platform;

/// <summary>
/// Hands due deliveries to dispatchers exactly once across all nodes: one statement selects due rows with <c>READPAST</c> (skipping
/// rows another node holds) and pushes their next-attempt time forward as a lease, so a crashed dispatcher's work reappears later.
/// Raw SQL is confined to this folder by the architecture tests.
/// </summary>
public sealed class WebhookStore
{
    private readonly AppDbContext _db;

    public WebhookStore(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Claims up to <paramref name="batch"/> due deliveries, at most <paramref name="perEndpoint"/> per endpoint (oldest first), so one
    /// endpoint's backlog cannot crowd out the others. The candidate list is computed without locks and the UPDATE re-checks "still due"
    /// under <c>UPDLOCK, READPAST</c>, so two nodes can never claim the same row.
    /// </summary>
    public async Task<IReadOnlyList<long>> ClaimDueAsync(int batch, int perEndpoint, DateTime now, TimeSpan lease, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE d SET NextAttemptAt = @lease
            OUTPUT inserted.Id
            FROM api.WebhookDeliveries AS d WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE d.Status = 'Pending' AND d.NextAttemptAt <= @now
              AND d.Id IN (
                  SELECT TOP (@batch) c.Id
                  FROM (SELECT Id, NextAttemptAt,
                               ROW_NUMBER() OVER (PARTITION BY EndpointId ORDER BY NextAttemptAt, Id) AS Position
                        FROM api.WebhookDeliveries WITH (READPAST)
                        WHERE Status = 'Pending' AND NextAttemptAt <= @now) AS c
                  WHERE c.Position <= @perEndpoint
                  ORDER BY c.NextAttemptAt, c.Id)
            """;

        await _db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = _db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = batch });
            command.Parameters.Add(new SqlParameter("@perEndpoint", SqlDbType.Int) { Value = perEndpoint });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now, Scale = 3 });
            command.Parameters.Add(new SqlParameter("@lease", SqlDbType.DateTime2) { Value = now + lease, Scale = 3 });

            var ids = new List<long>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetInt64(0));
            }

            return ids;
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }
}
