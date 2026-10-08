using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.Licensing;

/// <summary>A defect found in a ledger. <c>EntryId</c> is the offending row, or null when only the license balance disagrees.</summary>
public sealed record LedgerProblem(long? EntryId, string Message);

/// <summary>
/// Walks one license's ledger row by row (possibly in batches) and checks the hash chain, each row's own hash and the balance
/// arithmetic. It is the single implementation of these rules: the per-license endpoint, the platform-wide job and the tests all use it.
/// </summary>
public sealed class LedgerChainVerifier
{
    private byte[] _previous = LicenseTransaction.Genesis;
    private int _balance;

    public long Entries { get; private set; }

    public void Accept(LicenseTransaction e, ICollection<LedgerProblem> problems)
    {
        if (!e.PrevHash.AsSpan().SequenceEqual(_previous))
        {
            problems.Add(new LedgerProblem(e.Id, $"Entry {e.Id}: chain broken (previous hash mismatch)."));
        }

        if (!e.ComputeHash().AsSpan().SequenceEqual(e.RowHash))
        {
            problems.Add(new LedgerProblem(e.Id, $"Entry {e.Id}: content does not match its hash."));
        }

        if (e.BalanceBefore != _balance)
        {
            problems.Add(new LedgerProblem(e.Id, $"Entry {e.Id}: balance before ({e.BalanceBefore}) does not follow the previous entry ({_balance})."));
        }

        if (e.BalanceAfter != e.BalanceBefore + e.Credits)
        {
            problems.Add(new LedgerProblem(e.Id, $"Entry {e.Id}: balance arithmetic is wrong."));
        }

        _balance = e.BalanceAfter;
        _previous = e.RowHash;
        Entries++;
    }

    public void Finish(int licenseRemaining, ICollection<LedgerProblem> problems)
    {
        if (_balance != licenseRemaining)
        {
            problems.Add(new LedgerProblem(null, $"Ledger balance ({_balance}) differs from the license's remaining credits ({licenseRemaining})."));
        }
    }
}

/// <summary>Verifies a complete in-memory ledger (the per-license endpoint and tests).</summary>
public static class LedgerVerifier
{
    public static IReadOnlyList<LedgerProblem> Analyse(int licenseRemaining, IEnumerable<LicenseTransaction> entries)
    {
        var problems = new List<LedgerProblem>();
        var verifier = new LedgerChainVerifier();
        foreach (var entry in entries)
        {
            verifier.Accept(entry, problems);
        }

        verifier.Finish(licenseRemaining, problems);
        return problems;
    }

    public static LedgerVerificationDto Verify(License license, IReadOnlyList<LicenseTransaction> entries)
    {
        var problems = Analyse(license.Remaining, entries);
        return new LedgerVerificationDto(license.Id, problems.Count == 0, entries.Count, problems.Select(p => p.Message).ToList());
    }
}

public sealed class LedgerVerificationOptions
{
    public const string SectionName = "Metering:LedgerVerification";

    /// <summary>Turns the nightly job off (tests; the on-demand endpoint keeps working).</summary>
    public bool Enabled { get; set; } = true;

    [Range(1, 168)]
    public int IntervalHours { get; set; } = 24;

    /// <summary>Wait after start-up before the first run, so a restart does not skip the check and a deploy is not slowed by it.</summary>
    [Range(0, 1440)]
    public int InitialDelayMinutes { get; set; } = 10;

    [Range(10, 5000)]
    public int LicenseBatchSize { get; set; } = 200;

    [Range(100, 20_000)]
    public int EntryBatchSize { get; set; } = 1000;

    /// <summary>A balance-only mismatch can be a charge that committed while the ledger was being read; it is re-checked this many times before it is reported.</summary>
    [Range(0, 5)]
    public int BalanceRecheckAttempts { get; set; } = 2;

    [Range(0, 5000)]
    public int RecheckDelayMilliseconds { get; set; } = 250;
}

public sealed record LicenseLedgerHead(Guid LicenseId, Guid ClientId, int Remaining);

/// <summary>Read-only access for the verifier: licenses in key order and ledger rows in batches (no tracking, no per-row round trips).</summary>
public interface ILedgerVerificationStore
{
    Task<IReadOnlyList<LicenseLedgerHead>> ListLicensesAsync(Guid? afterLicenseId, int take, CancellationToken cancellationToken);

    Task<LicenseLedgerHead?> GetHeadAsync(Guid licenseId, CancellationToken cancellationToken);

    Task<IReadOnlyList<LicenseTransaction>> ListEntriesAsync(Guid licenseId, long afterEntryId, int take, CancellationToken cancellationToken);
}

/// <summary>A cross-node mutual-exclusion lock (implemented over the database); null from <see cref="TryAcquireAsync"/> means another node holds it.</summary>
public interface IDistributedLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Allows one platform-wide verification at a time per process (the endpoint and the nightly job share it).</summary>
public sealed class LedgerVerificationGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool TryEnter() => _gate.Wait(0);

    public void Exit() => _gate.Release();
}

public interface ILedgerVerificationService
{
    /// <summary>Verifies every license's ledger (platform scope). Returns the first broken row of each license that fails.</summary>
    Task<Result<LedgerVerificationReportDto>> VerifyAllAsync(CancellationToken cancellationToken);
}

public sealed partial class LedgerVerificationService : ILedgerVerificationService
{
    private readonly ILedgerVerificationStore _store;
    private readonly LedgerVerificationGate _gate;
    private readonly IDistributedLock? _lock;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly Microsoft.Extensions.Options.IOptions<LedgerVerificationOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<LedgerVerificationService> _logger;

    public LedgerVerificationService(
        ILedgerVerificationStore store,
        LedgerVerificationGate gate,
        IAuditService audit,
        IUnitOfWork unitOfWork,
        Microsoft.Extensions.Options.IOptions<LedgerVerificationOptions> options,
        TimeProvider time,
        ILogger<LedgerVerificationService> logger,
        IDistributedLock? distributedLock = null)
    {
        _lock = distributedLock;
        _store = store;
        _gate = gate;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<Result<LedgerVerificationReportDto>> VerifyAllAsync(CancellationToken cancellationToken)
    {
        if (!_gate.TryEnter())
        {
            return Error.Conflict(ErrorCodes.Conflict, "A ledger verification is already running.");
        }

        IAsyncDisposable? cluster = null;
        try
        {
            if (_lock is not null && (cluster = await _lock.TryAcquireAsync("ledger-verification", cancellationToken)) is null)
            {
                return Error.Conflict(ErrorCodes.Conflict, "A ledger verification is already running on another node.");
            }

            var options = _options.Value;
            var started = _time.GetUtcNow().UtcDateTime;
            var breaks = new List<LedgerBreakDto>();
            var licenses = 0;
            long entries = 0;
            Guid? after = null;

            while (true)
            {
                var batch = await _store.ListLicensesAsync(after, options.LicenseBatchSize, cancellationToken);
                foreach (var head in batch)
                {
                    long count;
                    LedgerProblem? problem;
                    try
                    {
                        (count, problem) = await VerifyLicenseAsync(head, options, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One unreadable license must not silence the check for every other license: report it and carry on.
                        _logger.LogError(ex, "License {LicenseId} could not be verified", head.LicenseId);
                        (count, problem) = (0, new LedgerProblem(null, "The ledger could not be read for verification."));
                    }

                    licenses++;
                    entries += count;
                    if (problem is not null)
                    {
                        breaks.Add(new LedgerBreakDto(head.LicenseId, head.ClientId, problem.EntryId, problem.Message));
                    }
                }

                if (batch.Count < options.LicenseBatchSize)
                {
                    break;
                }

                after = batch[^1].LicenseId;
            }

            await ReportBreaksAsync(breaks, cancellationToken);
            return new LedgerVerificationReportDto(started, _time.GetUtcNow().UtcDateTime, licenses, entries, breaks.Count, breaks);
        }
        finally
        {
            if (cluster is not null)
            {
                await cluster.DisposeAsync();
            }

            _gate.Exit();
        }
    }

    private async Task<(long Entries, LedgerProblem? Problem)> VerifyLicenseAsync(LicenseLedgerHead head, LedgerVerificationOptions options, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            var problems = new List<LedgerProblem>();
            var verifier = new LedgerChainVerifier();
            long afterEntry = 0;
            while (true)
            {
                var batch = await _store.ListEntriesAsync(head.LicenseId, afterEntry, options.EntryBatchSize, cancellationToken);
                foreach (var entry in batch)
                {
                    verifier.Accept(entry, problems);
                }

                if (batch.Count < options.EntryBatchSize)
                {
                    break;
                }

                afterEntry = batch[^1].Id;
            }

            // Read the balance AFTER the ledger so a charge that committed in between shows up as a (re-checked) balance mismatch only.
            var current = await _store.GetHeadAsync(head.LicenseId, cancellationToken) ?? head;
            verifier.Finish(current.Remaining, problems);

            if (problems.FirstOrDefault(p => p.EntryId is not null) is { } rowProblem)
            {
                return (verifier.Entries, rowProblem); // history is append-only: a bad row never heals, no re-check
            }

            if (problems.Count == 0)
            {
                return (verifier.Entries, null);
            }

            if (attempt++ >= options.BalanceRecheckAttempts)
            {
                return (verifier.Entries, problems[0]);
            }

            await Task.Delay(options.RecheckDelayMilliseconds, cancellationToken);
        }
    }

    private async Task ReportBreaksAsync(IReadOnlyList<LedgerBreakDto> breaks, CancellationToken cancellationToken)
    {
        if (breaks.Count == 0)
        {
            return;
        }

        foreach (var b in breaks)
        {
            LogBreak(_logger, b.LicenseId, b.ClientId, b.FirstBrokenEntryId, b.Reason);
            _audit.Record(new AuditEntry("ledger.verification_failed", nameof(License), b.LicenseId.ToString(), b.ClientId,
                NewValues: new { b.FirstBrokenEntryId, b.Reason }));
        }

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The finding itself is already logged at Critical; failing to persist the audit row must not hide it from the caller.
            _logger.LogError(ex, "Could not write the ledger verification audit entries");
        }
    }

    [LoggerMessage(EventId = 7001, Level = LogLevel.Critical,
        Message = "LEDGER TAMPERING SUSPECTED: license {LicenseId} (client {ClientId}) fails verification at entry {EntryId}: {Reason}")]
    private static partial void LogBreak(ILogger logger, Guid licenseId, Guid clientId, long? entryId, string reason);
}
