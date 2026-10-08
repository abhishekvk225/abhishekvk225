using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Infrastructure.Platform;

/// <summary>
/// The atomic half of metering: consumption is ONE conditional UPDATE … OUTPUT, enlisted in the caller's transaction. The row
/// lock it takes also serialises ledger appends for the license. Raw SQL is confined to this folder by the architecture tests.
/// </summary>
public sealed class MeteringStore : IMeteringStore
{
    private readonly Persistence.AppDbContext _db;

    public MeteringStore(Persistence.AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<License>> GetUsableAsync(Guid clientId, DateTime now, CancellationToken cancellationToken) =>
        await _db.Licenses.AsNoTracking()
            .Where(l => l.ClientId == clientId && l.Status == LicenseStatus.Active && l.StartsAt <= now && l.ExpiresAt > now && l.TotalCredits - l.ConsumedCredits > 0)
            .OrderBy(l => l.ExpiresAt).ThenBy(l => l.CreatedAt).ThenBy(l => l.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<LicenseAvailability>> GetAvailabilityAsync(Guid clientId, DateTime now, CancellationToken cancellationToken)
    {
        var rows = await _db.Licenses.AsNoTracking().Where(l => l.ClientId == clientId)
            .Select(l => new { l.Status, l.StartsAt, l.ExpiresAt, l.TotalCredits, l.ConsumedCredits })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new LicenseAvailability(r.Status, r.StartsAt <= now && now < r.ExpiresAt, now >= r.ExpiresAt, r.TotalCredits - r.ConsumedCredits)).ToList();
    }

    public async Task<ConsumeResult?> TryConsumeAsync(Guid licenseId, Guid clientId, int cost, DateTime now, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE licensing.Licenses
            SET ConsumedCredits = ConsumedCredits + @cost, UpdatedAt = @now
            OUTPUT (inserted.TotalCredits - inserted.ConsumedCredits) + @cost, inserted.TotalCredits - inserted.ConsumedCredits, inserted.ExpiresAt
            WHERE Id = @id AND ClientId = @client AND Status = 'Active'
              AND StartsAt <= @now AND ExpiresAt > @now
              AND TotalCredits - ConsumedCredits >= @cost
            """;

        await _db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = _db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
            command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = licenseId });
            command.Parameters.Add(new SqlParameter("@client", SqlDbType.UniqueIdentifier) { Value = clientId });
            command.Parameters.Add(new SqlParameter("@cost", SqlDbType.Int) { Value = cost });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now, Scale = 3 });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return new ConsumeResult(reader.GetInt32(0), reader.GetInt32(1), reader.GetDateTime(2));
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }
}
