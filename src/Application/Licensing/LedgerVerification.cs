using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.Licensing;

/// <summary>
/// A defect found in a ledger. <c>EntryId</c> is the offending row, or null when only the license balance (or a missing row) is
/// the problem. <c>Key</c> identifies WHAT is broken so a persistent break is reported once, not every night.
/// </summary>
public sealed record LedgerProblem(long? EntryId, string Message, string? Key = null)
{
    public const string BalanceKey = "balance";

    public string BreakKey => Key ?? (EntryId is { } id ? $"row:{id}" : BalanceKey);

    /// <summary>A balance-only mismatch can be a charge that committed while the ledger was read; everything else is permanent.</summary>
    public bool IsTransient => BreakKey == BalanceKey;
}

/// <summary>
/// Walks one license's ledger row by row (possibly in batches) and checks the hash chain, each row's own hash, the balance
/// arithmetic and — when signed checkpoints are supplied — that every checkpoint still matches the row it was taken at. It is the
/// single implementation of these rules: the per-license endpoint, the platform-wide job and the tests all use it.
/// </summary>
public sealed class LedgerChainVerifier
{
    private readonly Dictionary<long, LedgerCheckpoint> _checkpoints;
    private readonly HashSet<long> _matched = [];
    private byte[] _previous = LicenseTransaction.Genesis;
    private int _balance;

    public LedgerChainVerifier(IEnumerable<LedgerCheckpoint>? checkpoints = null)
    {
        _checkpoints = (checkpoints ?? []).GroupBy(c => c.LastEntryId).ToDictionary(g => g.Key, g => g.First());
    }

    public long Entries { get; private set; }

    /// <summary>Id, hash and resulting balance of the newest row accepted so far (what a new checkpoint would cover).</summary>
    public long LastEntryId { get; private set; }

    public byte[] LastHash => _previous;

    public int LastBalance => _balance;

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
        LastEntryId = e.Id;
        Entries++;

        // The checkpoint is signed with a key the database does not hold: a ledger rewritten with a recomputed (unkeyed) chain
        // still cannot reproduce the head hash, count and balance it recorded.
        if (_checkpoints.TryGetValue(e.Id, out var checkpoint))
        {
            _matched.Add(e.Id);
            if (checkpoint.EntryCount != Entries || !checkpoint.HeadHash.AsSpan().SequenceEqual(e.RowHash) || checkpoint.BalanceAfter != e.BalanceAfter)
            {
                problems.Add(new LedgerProblem(
                    e.Id,
                    $"Entry {e.Id}: does not match the signed checkpoint taken at {checkpoint.CreatedAt:yyyy-MM-dd HH:mm} UTC (the ledger was rewritten after it was anchored).",
                    $"checkpoint:{checkpoint.LastEntryId}"));
            }
        }
    }

    public void Finish(int licenseRemaining, ICollection<LedgerProblem> problems)
    {
        if (_balance != licenseRemaining)
        {
            problems.Add(new LedgerProblem(null, $"Ledger balance ({_balance}) differs from the license's remaining credits ({licenseRemaining})."));
        }

        foreach (var missing in _checkpoints.Values.Where(c => !_matched.Contains(c.LastEntryId)).OrderBy(c => c.LastEntryId))
        {
            problems.Add(new LedgerProblem(
                null,
                $"The signed checkpoint at entry {missing.LastEntryId} ({missing.EntryCount} rows) has no matching ledger row: rows were deleted or the ledger was truncated.",
                $"checkpoint:{missing.LastEntryId}"));
        }
    }
}

/// <summary>Verifies a complete in-memory ledger (the per-license endpoint and tests).</summary>
public static class LedgerVerifier
{
    public static IReadOnlyList<LedgerProblem> Analyse(int licenseRemaining, IEnumerable<LicenseTransaction> entries, IEnumerable<LedgerCheckpoint>? checkpoints = null)
    {
        var problems = new List<LedgerProblem>();
        var verifier = new LedgerChainVerifier(checkpoints);
        foreach (var entry in entries)
        {
            verifier.Accept(entry, problems);
        }

        verifier.Finish(licenseRemaining, problems);
        return problems;
    }

    public static LedgerVerificationDto Verify(License license, IReadOnlyList<LicenseTransaction> entries, IEnumerable<LedgerCheckpoint>? checkpoints = null,
        IEnumerable<LedgerProblem>? extraProblems = null)
    {
        var problems = Analyse(license.Remaining, entries, checkpoints).Concat(extraProblems ?? []).ToList();
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

/// <summary>Read access for the verifier (licenses in key order, ledger rows in batches, no tracking) plus the small amount of state it keeps.</summary>
public interface ILedgerVerificationStore
{
    Task<IReadOnlyList<LicenseLedgerHead>> ListLicensesAsync(Guid? afterLicenseId, int take, CancellationToken cancellationToken);

    Task<LicenseLedgerHead?> GetHeadAsync(Guid licenseId, CancellationToken cancellationToken);

    Task<IReadOnlyList<LicenseTransaction>> ListEntriesAsync(Guid licenseId, long afterEntryId, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<LedgerCheckpoint>> ListCheckpointsAsync(Guid licenseId, CancellationToken cancellationToken);

    /// <summary>Licenses that have signed checkpoints but no longer exist (a whole license, ledger included, was deleted).</summary>
    Task<IReadOnlyList<LicenseLedgerHead>> ListOrphanCheckpointLicensesAsync(CancellationToken cancellationToken);

    void Add(LedgerCheckpoint checkpoint);

    Task<IReadOnlyList<LedgerBreakRecord>> ListOpenBreaksAsync(CancellationToken cancellationToken);

    void Add(LedgerBreakRecord record);

    void Add(LedgerVerificationRun run);

    Task<LedgerVerificationRun?> GetRunAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<LedgerVerificationRun>> ListRunningAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<LedgerVerificationRun>> ListRecentRunsAsync(int take, CancellationToken cancellationToken);
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

/// <summary>Hands a started run to a background worker that finishes it after the HTTP request has returned and releases the lease.</summary>
public interface ILedgerRunLauncher
{
    void Launch(Guid runId, Guid? licenseId, IAsyncDisposable lease);
}

public interface ILedgerVerificationService
{
    /// <summary>
    /// Starts a verification in the background and returns immediately with its run id. 409 when a run is already active on any
    /// node. <paramref name="licenseId"/> limits the run to one license.
    /// </summary>
    Task<Result<LedgerRunDto>> StartAsync(Guid? licenseId, CancellationToken cancellationToken);

    Task<Result<LedgerRunDto>> GetRunAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<LedgerRunDto>>> ListRunsAsync(CancellationToken cancellationToken);

    /// <summary>Runs a verification to completion in the caller's context (the nightly job, tests) under the same one-at-a-time lease.</summary>
    Task<Result<LedgerVerificationReportDto>> RunAsync(string trigger, Guid? licenseId, CancellationToken cancellationToken);

    /// <summary>Does the work of an already started run (called by the launcher, which owns the lease). Never throws: failures are recorded on the run.</summary>
    Task ExecuteRunAsync(Guid runId, Guid? licenseId, CancellationToken cancellationToken);
}

/// <summary>Creates and checks the keyed anchors (the HMAC lives in the platform crypto, never in the database).</summary>
public sealed class LedgerAnchorService
{
    private readonly IPlatformCrypto _crypto;
    private readonly ILedgerVerificationStore _store;
    private readonly TimeProvider _time;

    public LedgerAnchorService(IPlatformCrypto crypto, ILedgerVerificationStore store, TimeProvider time)
    {
        _crypto = crypto;
        _store = store;
        _time = time;
    }

    public LedgerCheckpoint Create(Guid licenseId, Guid clientId, LedgerChainVerifier verified) =>
        LedgerCheckpoint.Create(licenseId, clientId, verified.LastEntryId, verified.Entries, verified.LastHash, verified.LastBalance, _crypto.KeyId,
            _time.GetUtcNow().UtcDateTime, bytes => _crypto.ComputeMac(CryptoPurposes.LedgerAnchor, bytes));

    public bool IsAuthentic(LedgerCheckpoint checkpoint) =>
        CryptographicOperations.FixedTimeEquals(checkpoint.Mac, _crypto.ComputeMac(CryptoPurposes.LedgerAnchor, checkpoint.CanonicalBytes()));

    /// <summary>The license's checkpoints split into the authentic ones (to compare against the ledger) and problems for forged ones.</summary>
    public async Task<(IReadOnlyList<LedgerCheckpoint> Authentic, IReadOnlyList<LedgerProblem> Forged)> LoadAsync(Guid licenseId, CancellationToken cancellationToken)
    {
        var authentic = new List<LedgerCheckpoint>();
        var forged = new List<LedgerProblem>();
        foreach (var checkpoint in await _store.ListCheckpointsAsync(licenseId, cancellationToken))
        {
            if (IsAuthentic(checkpoint))
            {
                authentic.Add(checkpoint);
            }
            else
            {
                forged.Add(new LedgerProblem(
                    null,
                    $"The signed checkpoint {checkpoint.Id} (entry {checkpoint.LastEntryId}) fails its signature check: it was altered, or the anchor key changed.",
                    $"checkpoint-signature:{checkpoint.Id}"));
            }
        }

        return (authentic, forged);
    }
}

public sealed partial class LedgerVerificationService : ILedgerVerificationService
{
    private const int MaxBreaksStored = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ILedgerVerificationStore _store;
    private readonly LedgerAnchorService _anchors;
    private readonly LedgerVerificationGate _gate;
    private readonly IDistributedLock? _lock;
    private readonly ILedgerRunLauncher _launcher;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly Microsoft.Extensions.Options.IOptions<LedgerVerificationOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<LedgerVerificationService> _logger;

    public LedgerVerificationService(
        ILedgerVerificationStore store,
        LedgerAnchorService anchors,
        LedgerVerificationGate gate,
        ILedgerRunLauncher launcher,
        ICurrentUser currentUser,
        IAuditService audit,
        IUnitOfWork unitOfWork,
        Microsoft.Extensions.Options.IOptions<LedgerVerificationOptions> options,
        TimeProvider time,
        ILogger<LedgerVerificationService> logger,
        IDistributedLock? distributedLock = null)
    {
        _lock = distributedLock;
        _store = store;
        _anchors = anchors;
        _gate = gate;
        _launcher = launcher;
        _currentUser = currentUser;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _options = options;
        _time = time;
        _logger = logger;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ starting and reading runs

    public async Task<Result<LedgerRunDto>> StartAsync(Guid? licenseId, CancellationToken cancellationToken)
    {
        if (licenseId is { } wanted && await _store.GetHeadAsync(wanted, cancellationToken) is null)
        {
            return Error.NotFound("The license was not found.");
        }

        var lease = await AcquireLeaseAsync(cancellationToken);
        if (lease.IsFailure)
        {
            return lease.Error!;
        }

        try
        {
            var run = await BeginRunAsync("manual", _currentUser.ActorId, licenseId, cancellationToken);
            _audit.Record(new AuditEntry("ledger.verification_requested", nameof(LedgerVerificationRun), run.Id.ToString(), null, NewValues: new { LicenseId = licenseId }));
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            _launcher.Launch(run.Id, licenseId, lease.Value); // ownership of the lease moves to the worker
            return ToDto(run);
        }
        catch
        {
            await lease.Value.DisposeAsync();
            throw;
        }
    }

    public async Task<Result<LedgerRunDto>> GetRunAsync(Guid id, CancellationToken cancellationToken) =>
        await _store.GetRunAsync(id, cancellationToken) is { } run ? ToDto(run) : Error.NotFound();

    public async Task<Result<IReadOnlyList<LedgerRunDto>>> ListRunsAsync(CancellationToken cancellationToken) =>
        Result<IReadOnlyList<LedgerRunDto>>.Success((await _store.ListRecentRunsAsync(20, cancellationToken)).Select(ToDto).ToList());

    public async Task<Result<LedgerVerificationReportDto>> RunAsync(string trigger, Guid? licenseId, CancellationToken cancellationToken)
    {
        var lease = await AcquireLeaseAsync(cancellationToken);
        if (lease.IsFailure)
        {
            return lease.Error!;
        }

        await using (lease.Value)
        {
            var run = await BeginRunAsync(trigger, null, licenseId, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            var report = await ExecuteAsync(run, licenseId, cancellationToken);
            return report;
        }
    }

    public async Task ExecuteRunAsync(Guid runId, Guid? licenseId, CancellationToken cancellationToken)
    {
        var run = await _store.GetRunAsync(runId, cancellationToken);
        if (run is null)
        {
            return;
        }

        await ExecuteAsync(run, licenseId, cancellationToken);
    }

    private async Task<Result<IAsyncDisposable>> AcquireLeaseAsync(CancellationToken cancellationToken)
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
                _gate.Exit();
                return Error.Conflict(ErrorCodes.Conflict, "A ledger verification is already running on another node.");
            }
        }
        catch
        {
            _gate.Exit();
            throw;
        }

        return Result<IAsyncDisposable>.Success(new Lease(_gate, cluster));
    }

    private async Task<LedgerVerificationRun> BeginRunAsync(string trigger, Guid? requestedBy, Guid? licenseId, CancellationToken cancellationToken)
    {
        var now = Now;

        // We hold the cluster-wide lease, so any other run still marked Running was cut short (its node stopped).
        foreach (var stale in await _store.ListRunningAsync(cancellationToken))
        {
            stale.Fail("Interrupted: the process running it stopped before it finished.", now);
        }

        var run = LedgerVerificationRun.Start(trigger, requestedBy, licenseId, now);
        _store.Add(run);
        return run;
    }

    // ------------------------------------------------------------------ the scan

    private sealed record Found(LedgerBreakDto Dto, string Key);

    private async Task<LedgerVerificationReportDto> ExecuteAsync(LedgerVerificationRun run, Guid? onlyLicense, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var started = Now;
        var breaks = new List<Found>();
        var checkedLicenses = new HashSet<Guid>();
        var licenses = 0;
        long entries = 0;
        try
        {
            IReadOnlyList<LicenseLedgerHead> work;
            if (onlyLicense is { } single)
            {
                work = await _store.GetHeadAsync(single, cancellationToken) is { } head ? [head] : [];
            }
            else
            {
                work = [];
            }

            Guid? after = null;
            while (true)
            {
                var batch = onlyLicense is null ? await _store.ListLicensesAsync(after, options.LicenseBatchSize, cancellationToken) : work;
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
                        (count, problem) = (0, new LedgerProblem(null, "The ledger could not be read for verification.", "unreadable"));
                    }

                    licenses++;
                    entries += count;
                    checkedLicenses.Add(head.LicenseId);
                    if (problem is not null)
                    {
                        breaks.Add(new Found(new LedgerBreakDto(head.LicenseId, head.ClientId, problem.EntryId, problem.Message), problem.BreakKey));
                    }
                }

                if (onlyLicense is not null || batch.Count < options.LicenseBatchSize)
                {
                    break;
                }

                after = batch[^1].LicenseId;
            }

            if (onlyLicense is null)
            {
                // Whole-license deletion: checkpoints outlive their license (they cannot be edited or removed through the application).
                foreach (var orphan in await _store.ListOrphanCheckpointLicensesAsync(cancellationToken))
                {
                    breaks.Add(new Found(
                        new LedgerBreakDto(orphan.LicenseId, orphan.ClientId, null, "The license no longer exists but its signed ledger checkpoints do: it was deleted together with its ledger."),
                        "license-missing"));
                }
            }

            await ReportBreaksAsync(breaks, onlyLicense is null ? null : checkedLicenses, cancellationToken);
            var report = new LedgerVerificationReportDto(started, Now, licenses, entries, breaks.Count, breaks.Select(b => b.Dto).ToList());
            run.Complete(licenses, entries, breaks.Count, JsonSerializer.Serialize(report.Breaks.Take(MaxBreaksStored), Json), Now);
            await _unitOfWork.SaveChangesAsync(CancellationToken.None);
            return report;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Ledger verification run {RunId} failed", run.Id);
            run.Fail("The verification could not be completed. See the server log.", Now);
            try
            {
                await _unitOfWork.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception saveError) when (saveError is not OperationCanceledException)
            {
                _logger.LogError(saveError, "Could not record the failure of ledger verification run {RunId}", run.Id);
            }

            return new LedgerVerificationReportDto(started, Now, licenses, entries, breaks.Count, breaks.Select(b => b.Dto).ToList());
        }
        catch (OperationCanceledException)
        {
            run.Fail("The verification was cancelled (the service is stopping).", Now);
            await _unitOfWork.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<(long Entries, LedgerProblem? Problem)> VerifyLicenseAsync(LicenseLedgerHead head, LedgerVerificationOptions options, CancellationToken cancellationToken)
    {
        var (authentic, forged) = await _anchors.LoadAsync(head.LicenseId, cancellationToken);
        var attempt = 0;
        while (true)
        {
            var problems = new List<LedgerProblem>(forged);
            var verifier = new LedgerChainVerifier(authentic);
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

            if (problems.FirstOrDefault(p => !p.IsTransient) is { } permanent)
            {
                return (verifier.Entries, permanent); // history is append-only: a bad row never heals, no re-check
            }

            if (problems.Count == 0)
            {
                // Anchor the head we just proved clean (never a head that failed), unless a checkpoint already covers it.
                if (verifier.Entries > 0 && authentic.All(c => c.LastEntryId != verifier.LastEntryId))
                {
                    var checkpoint = _anchors.Create(head.LicenseId, head.ClientId, verifier);
                    _store.Add(checkpoint);
                    LogCheckpoint(_logger, head.LicenseId, checkpoint.LastEntryId, checkpoint.EntryCount, Convert.ToHexString(checkpoint.HeadHash), Convert.ToHexString(checkpoint.Mac));
                }

                return (verifier.Entries, null);
            }

            if (attempt++ >= options.BalanceRecheckAttempts)
            {
                return (verifier.Entries, problems[0]);
            }

            await Task.Delay(options.RecheckDelayMilliseconds, cancellationToken);
        }
    }

    /// <summary>
    /// Every run logs each break at Critical (the operator alert), but the audit trail gets ONE entry per (license, broken thing):
    /// a break that stays broken is counted on its record, not re-audited every night. A license that verifies clean again closes its records.
    /// </summary>
    private async Task ReportBreaksAsync(IReadOnlyList<Found> breaks, HashSet<Guid>? onlyChecked, CancellationToken cancellationToken)
    {
        var now = Now;
        var open = (await _store.ListOpenBreaksAsync(cancellationToken)).ToDictionary(r => (r.LicenseId, r.BreakKey));
        var current = breaks.Select(b => (b.Dto.LicenseId, b.Key)).ToHashSet();

        foreach (var b in breaks)
        {
            LogBreak(_logger, b.Dto.LicenseId, b.Dto.ClientId, b.Dto.FirstBrokenEntryId, b.Dto.Reason);
            if (open.TryGetValue((b.Dto.LicenseId, b.Key), out var existing))
            {
                existing.Seen(now);
                continue;
            }

            _store.Add(LedgerBreakRecord.Open(b.Dto.LicenseId, b.Dto.ClientId, b.Key, b.Dto.Reason, now));
            _audit.Record(new AuditEntry("ledger.verification_failed", nameof(License), b.Dto.LicenseId.ToString(), b.Dto.ClientId,
                NewValues: new { b.Dto.FirstBrokenEntryId, b.Dto.Reason, BreakKey = b.Key }));
        }

        foreach (var record in open.Values.Where(r => !current.Contains((r.LicenseId, r.BreakKey)) && (onlyChecked is null || onlyChecked.Contains(r.LicenseId))))
        {
            record.Clear(now);
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

    private static LedgerRunDto ToDto(LedgerVerificationRun r) => new(
        r.Id, r.Status.ToString(), r.Trigger, r.LicenseId, r.StartedAt, r.FinishedAt, r.LicensesChecked, r.EntriesChecked, r.BrokenLicenses,
        r.BreaksJson is null ? [] : JsonSerializer.Deserialize<List<LedgerBreakDto>>(r.BreaksJson, Json) ?? [], r.Error);

    [LoggerMessage(EventId = 7001, Level = LogLevel.Critical,
        Message = "LEDGER TAMPERING SUSPECTED: license {LicenseId} (client {ClientId}) fails verification at entry {EntryId}: {Reason}")]
    private static partial void LogBreak(ILogger logger, Guid licenseId, Guid clientId, long? entryId, string reason);

    // Shipped to the log pipeline on purpose: it is an off-database copy of the anchor (the MAC is not secret, the key is).
    [LoggerMessage(EventId = 7002, Level = LogLevel.Information,
        Message = "Ledger checkpoint written: license {LicenseId} entry {LastEntryId} rows {EntryCount} head {HeadHash} mac {Mac}")]
    private static partial void LogCheckpoint(ILogger logger, Guid licenseId, long lastEntryId, long entryCount, string headHash, string mac);

    private sealed class Lease : IAsyncDisposable
    {
        private readonly LedgerVerificationGate _gate;
        private readonly IAsyncDisposable? _cluster;
        private int _disposed;

        public Lease(LedgerVerificationGate gate, IAsyncDisposable? cluster)
        {
            _gate = gate;
            _cluster = cluster;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                if (_cluster is not null)
                {
                    await _cluster.DisposeAsync();
                }
            }
            finally
            {
                _gate.Exit();
            }
        }
    }
}
