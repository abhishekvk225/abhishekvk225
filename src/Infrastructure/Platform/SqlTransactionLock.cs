using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexaVerify.Application.Abstractions;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Platform;

/// <summary>
/// Transaction-scoped SQL Server application lock (<c>sp_getapplock</c> with owner <c>Transaction</c>): released automatically at
/// commit or rollback, so a crash can never leave it held. Used to make "count, then insert" caps atomic.
/// </summary>
public sealed class SqlTransactionLock : ITransactionLock
{
    private const int TimeoutMilliseconds = 10_000;

    private readonly AppDbContext _db;

    public SqlTransactionLock(AppDbContext db)
    {
        _db = db;
    }

    public async Task AcquireAsync(string name, CancellationToken cancellationToken)
    {
        var transaction = _db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("A transaction lock can only be taken inside a transaction.");

        await using var command = _db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "EXEC @result = sp_getapplock @Resource = @name, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = @timeout";
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 255) { Value = "nexaverify:cap:" + name });
        command.Parameters.Add(new SqlParameter("@timeout", SqlDbType.Int) { Value = TimeoutMilliseconds });
        var result = new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.Output };
        command.Parameters.Add(result);
        await command.ExecuteNonQueryAsync(cancellationToken);
        if ((int)result.Value < 0)
        {
            throw new TimeoutException("Could not obtain the transaction lock '" + name + "'.");
        }
    }
}
