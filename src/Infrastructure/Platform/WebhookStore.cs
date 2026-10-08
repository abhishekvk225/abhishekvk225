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

    public async Task<IReadOnlyList<long>> ClaimDueAsync(int batch, DateTime now, TimeSpan lease, CancellationToken cancellationToken)
    {
        const string sql = """
            ;WITH due AS (
                SELECT TOP (@batch) Id, NextAttemptAt
                FROM api.WebhookDeliveries WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE Status = 'Pending' AND NextAttemptAt <= @now
                ORDER BY NextAttemptAt, Id)
            UPDATE due SET NextAttemptAt = @lease
            OUTPUT inserted.Id
            """;

        await _db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = _db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = batch });
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
