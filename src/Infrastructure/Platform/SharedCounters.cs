using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NexaVerify.Infrastructure.Platform;

/// <summary>Settings of the shared rate-limit/quota counters (<c>Counters:*</c>, deploy/CONFIG.md).</summary>
public sealed class SharedCounterOptions
{
    public const string SectionName = "Counters";

    /// <summary>
    /// Share the counters between API nodes through SQL Server. When off (single node, tests) every node counts on its own, which
    /// multiplies the effective limits by the number of nodes.
    /// </summary>
    public bool Shared { get; set; } = true;

    /// <summary>
    /// A node asks the database for up to <c>limit / ReservationDivisor</c> permits at a time (at least 1, at most
    /// <see cref="MaxReservation"/>) and hands them out from memory, so most calls never touch the database. Total grants never exceed the
    /// limit; permits reserved by a node that then goes quiet are lost for that window, which is why small limits reserve one at a time.
    /// </summary>
    [Range(1, 10_000)]
    public int ReservationDivisor { get; set; } = 20;

    [Range(1, 10_000)]
    public int MaxReservation { get; set; } = 50;

    /// <summary>After the database refused a permit, calls for the same key are refused from memory for this long before asking again.</summary>
    [Range(0, 60)]
    public int ExhaustedRecheckSeconds { get; set; } = 2;

    /// <summary>Per-minute and throttle buckets older than this are deleted.</summary>
    [Range(5, 1440)]
    public int ShortBucketRetentionMinutes { get; set; } = 60;

    /// <summary>Daily buckets older than this are deleted.</summary>
    [Range(2, 30)]
    public int DailyBucketRetentionDays { get; set; } = 3;

    [Range(1, 1440)]
    public int PurgeIntervalMinutes { get; set; } = 10;
}

/// <summary>Where the authoritative counters live. The SQL implementation is atomic across nodes.</summary>
public interface ICounterBackend
{
    /// <summary>
    /// Atomically takes up to <paramref name="requested"/> permits from bucket (<paramref name="kind"/>, <paramref name="key"/>,
    /// <paramref name="bucket"/>) without ever letting the bucket's total pass <paramref name="limit"/>. Returns how many were granted (0 = exhausted).
    /// </summary>
    Task<long> ReserveAsync(byte kind, Guid key, long bucket, long requested, long limit, DateTime now, CancellationToken cancellationToken);

    /// <summary>Deletes counters last touched before the cut-offs; returns the rows removed.</summary>
    Task<int> PurgeAsync(DateTime shortBefore, DateTime dailyBefore, CancellationToken cancellationToken);
}

/// <summary>Where the SQL counters are (a dedicated pooled connection: the table is not tenant data, so no session context is needed).</summary>
public sealed record SharedCounterConnection(string ConnectionString);

/// <summary>
/// Single-statement upsert: UPDATE with the grant computed in the same statement (row lock = atomicity); if the bucket row does not
/// exist yet INSERT it, and if a concurrent node won that race (duplicate key) take from its row instead. No transaction is needed.
/// </summary>
public sealed class SqlCounterBackend : ICounterBackend
{
    private const string Reserve = """
        SET NOCOUNT ON;
        DECLARE @granted bigint = 0;
        UPDATE api.UsageCounters
           SET @granted = CASE WHEN Used >= @limit THEN 0 WHEN Used + @req > @limit THEN @limit - Used ELSE @req END,
               Used = Used + CASE WHEN Used >= @limit THEN 0 WHEN Used + @req > @limit THEN @limit - Used ELSE @req END,
               UpdatedAt = @now
         WHERE Kind = @kind AND KeyId = @key AND Bucket = @bucket;
        IF @@ROWCOUNT = 0
        BEGIN
            BEGIN TRY
                SET @granted = CASE WHEN @req > @limit THEN @limit ELSE @req END;
                INSERT INTO api.UsageCounters (Kind, KeyId, Bucket, Used, UpdatedAt) VALUES (@kind, @key, @bucket, @granted, @now);
            END TRY
            BEGIN CATCH
                IF ERROR_NUMBER() IN (2601, 2627)
                BEGIN
                    UPDATE api.UsageCounters
                       SET @granted = CASE WHEN Used >= @limit THEN 0 WHEN Used + @req > @limit THEN @limit - Used ELSE @req END,
                           Used = Used + CASE WHEN Used >= @limit THEN 0 WHEN Used + @req > @limit THEN @limit - Used ELSE @req END,
                           UpdatedAt = @now
                     WHERE Kind = @kind AND KeyId = @key AND Bucket = @bucket;
                END
                ELSE THROW;
            END CATCH
        END
        SELECT @granted;
        """;

    private const string PurgeSql = """
        DELETE TOP (5000) FROM api.UsageCounters
         WHERE (Kind <> 2 AND UpdatedAt < @shortBefore) OR (Kind = 2 AND UpdatedAt < @dailyBefore);
        """;

    private readonly SharedCounterConnection _connection;

    public SqlCounterBackend(SharedCounterConnection connection)
    {
        _connection = connection;
    }

    public async Task<long> ReserveAsync(byte kind, Guid key, long bucket, long requested, long limit, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connection.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Reserve;
        command.Parameters.Add(new SqlParameter("@kind", SqlDbType.TinyInt) { Value = kind });
        command.Parameters.Add(new SqlParameter("@key", SqlDbType.UniqueIdentifier) { Value = key });
        command.Parameters.Add(new SqlParameter("@bucket", SqlDbType.BigInt) { Value = bucket });
        command.Parameters.Add(new SqlParameter("@req", SqlDbType.BigInt) { Value = requested });
        command.Parameters.Add(new SqlParameter("@limit", SqlDbType.BigInt) { Value = limit });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now, Scale = 3 });
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<int> PurgeAsync(DateTime shortBefore, DateTime dailyBefore, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connection.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var total = 0;
        int removed;
        do
        {
            await using var command = connection.CreateCommand();
            command.CommandText = PurgeSql;
            command.Parameters.Add(new SqlParameter("@shortBefore", SqlDbType.DateTime2) { Value = shortBefore, Scale = 3 });
            command.Parameters.Add(new SqlParameter("@dailyBefore", SqlDbType.DateTime2) { Value = dailyBefore, Scale = 3 });
            removed = await command.ExecuteNonQueryAsync(cancellationToken);
            total += removed;
        }
        while (removed == 5000);

        return total;
    }
}

/// <summary>
/// Hands out permits for bucketed counters. The counters are shared through <see cref="ICounterBackend"/>; a node keeps the permits
/// it reserved in memory (the fast path), so the database is touched roughly once per <c>limit / ReservationDivisor</c> calls. If the
/// backend is unavailable the node degrades to counting on its own (availability over exactness) and logs it, rate-limited.
/// </summary>
public sealed class SharedWindowCounters
{
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan IdleLeaseLifetime = TimeSpan.FromMinutes(15);

    private readonly ICounterBackend _backend;
    private readonly SharedCounterOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SharedWindowCounters> _logger;
    private readonly ConcurrentDictionary<(byte Kind, Guid Key), Lease> _leases = new();
    private long _lastFailureLogTicks;
    private long _lastSweepTicks;

    public SharedWindowCounters(ICounterBackend backend, IOptions<SharedCounterOptions> options, TimeProvider time, ILogger<SharedWindowCounters> logger)
    {
        _backend = backend;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>Number of keys tracked in memory (diagnostics and tests).</summary>
    public int TrackedKeys => _leases.Count;

    private sealed class Lease
    {
        public readonly object Gate = new();
        public long Bucket = long.MinValue;
        public long Available;
        public long LocalUsed;
        public long ExhaustedLimit;
        public DateTime ExhaustedUntil;
        public DateTime LastTouched;
    }

    /// <summary>Takes one permit; false when the bucket's <paramref name="limit"/> is used up (on any node).</summary>
    public async Task<bool> TryTakeAsync(byte kind, Guid key, long bucket, long limit, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        Sweep(now);
        var lease = _leases.GetOrAdd((kind, key), _ => new Lease());
        lock (lease.Gate)
        {
            lease.LastTouched = now;
            if (lease.Bucket != bucket)
            {
                lease.Bucket = bucket;
                lease.Available = 0;
                lease.LocalUsed = 0;
                lease.ExhaustedUntil = DateTime.MinValue;
            }

            if (lease.Available > 0)
            {
                lease.Available--;
                return true;
            }

            if (lease.ExhaustedLimit == limit && now < lease.ExhaustedUntil)
            {
                return false;
            }
        }

        var request = Math.Min(Math.Clamp(limit / _options.ReservationDivisor, 1, _options.MaxReservation), limit);
        long granted;
        try
        {
            granted = _options.Shared ? await _backend.ReserveAsync(kind, key, bucket, request, limit, now, cancellationToken) : -1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            granted = -1;
            LogFailure(ex, now);
        }

        lock (lease.Gate)
        {
            if (lease.Bucket != bucket)
            {
                // The window rolled over while we waited for the database: the permit belongs to the old window and is spent on this call.
                return granted != 0;
            }

            if (granted < 0)
            {
                // Per-node counting (sharing is off or the database is unreachable): same arithmetic, local state only.
                granted = Math.Max(0, Math.Min(request, limit - lease.LocalUsed));
                lease.LocalUsed += granted;
            }

            if (granted == 0)
            {
                lease.ExhaustedLimit = limit;
                lease.ExhaustedUntil = now.AddSeconds(_options.ExhaustedRecheckSeconds);
                return false;
            }

            lease.Available += granted - 1;
            return true;
        }
    }

    /// <summary>Gives a permit back (the call it was taken for was refused by a later check). Only the local reservation is credited.</summary>
    public void Return(byte kind, Guid key, long bucket)
    {
        if (_leases.TryGetValue((kind, key), out var lease))
        {
            lock (lease.Gate)
            {
                if (lease.Bucket == bucket)
                {
                    lease.Available++;
                }
            }
        }
    }

    private void LogFailure(Exception ex, DateTime now)
    {
        var last = Interlocked.Read(ref _lastFailureLogTicks);
        if (now.Ticks - last >= FailureLogInterval.Ticks && Interlocked.CompareExchange(ref _lastFailureLogTicks, now.Ticks, last) == last)
        {
            _logger.LogWarning(ex, "Shared rate-limit counters are unavailable; this node is counting on its own until the database answers again");
        }
    }

    /// <summary>Forgets leases that have been idle, so the map cannot grow without bound (a new call simply creates a fresh lease).</summary>
    private void Sweep(DateTime now)
    {
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.Ticks - last < TimeSpan.TicksPerMinute * 5 || Interlocked.CompareExchange(ref _lastSweepTicks, now.Ticks, last) != last)
        {
            return;
        }

        foreach (var (id, lease) in _leases)
        {
            lock (lease.Gate)
            {
                if (now - lease.LastTouched > IdleLeaseLifetime)
                {
                    _leases.TryRemove(id, out _);
                }
            }
        }
    }
}
