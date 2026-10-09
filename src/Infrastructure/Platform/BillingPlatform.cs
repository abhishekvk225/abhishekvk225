using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Billing;
using NexaVerify.Domain.Api;
using NexaVerify.Domain.Billing;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Platform;

/// <summary>
/// Hands out <c>INV-YYYY-000001</c> numbers with one atomic MERGE on the year's row. The row stays locked until the surrounding transaction
/// ends, so two payments can never get the same number, and a payment that rolls back gives its number back: the sequence has no gaps.
/// </summary>
public sealed class InvoiceNumberAllocator : IInvoiceNumberAllocator
{
    private const string Sql =
        """
        MERGE billing.InvoiceSequences WITH (HOLDLOCK) AS target
        USING (SELECT @year AS [Year]) AS source ON target.[Year] = source.[Year]
        WHEN MATCHED THEN UPDATE SET LastNumber = target.LastNumber + 1
        WHEN NOT MATCHED THEN INSERT ([Year], LastNumber) VALUES (@year, 1)
        OUTPUT inserted.LastNumber;
        """;

    private readonly AppDbContext _db;

    public InvoiceNumberAllocator(AppDbContext db)
    {
        _db = db;
    }

    public async Task<string> NextAsync(int year, CancellationToken cancellationToken)
    {
        var transaction = _db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Invoice numbers must be allocated inside the transaction that marks the order paid.");

        var connection = _db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = Sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@year";
        parameter.Value = year;
        command.Parameters.Add(parameter);

        var number = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        return InvoiceSequence.Format(year, number);
    }
}

/// <summary>
/// Hourly checkout budgets per user and per client, in the shared counters (they hold across API nodes). Both budgets are charged for every
/// attempt, so one busy user can not use up the client's whole budget unnoticed and a client can not multiply its budget with more users.
/// </summary>
public sealed class BillingThrottle : IBillingThrottle
{
    private readonly SharedWindowCounters _counters;
    private readonly BillingOptions _options;
    private readonly TimeProvider _time;

    public BillingThrottle(SharedWindowCounters counters, IOptions<BillingOptions> options, TimeProvider time)
    {
        _counters = counters;
        _options = options.Value;
        _time = time;
    }

    public async Task<bool> TryAcquireCheckoutAsync(Guid userId, Guid clientId, CancellationToken cancellationToken)
    {
        var hour = _time.GetUtcNow().UtcDateTime.Ticks / TimeSpan.TicksPerHour;
        var user = await _counters.TryTakeAsync(UsageCounterKinds.Day, KeyFor("checkout-user", userId), hour, _options.MaxCheckoutsPerUserPerHour, cancellationToken);
        if (!user)
        {
            return false;
        }

        return await _counters.TryTakeAsync(UsageCounterKinds.Day, KeyFor("checkout-client", clientId), hour, _options.MaxCheckoutsPerClientPerHour, cancellationToken);
    }

    /// <summary>A stable 128-bit key for (budget, principal); the counter table stores nothing readable.</summary>
    public static Guid KeyFor(string budget, Guid id) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("billing:" + budget + ":" + id.ToString("N")))[..16]);
}
