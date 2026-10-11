using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Licensing;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Tenancy;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.Infrastructure.Security;

namespace NexaVerify.Infrastructure.Platform.KeyRotation;

public sealed record RotationRequest(bool DryRun, int BatchSize = 200, int? MaxBatches = null, bool Resume = false, bool IncludeLedger = true)
{
    public const int MaxBatchSize = 5000;
}

public enum RotationOutcome
{
    /// <summary>Everything is under the current key.</summary>
    Complete = 0,

    /// <summary>The batch budget ran out (or contention kept rows from settling); run again with --resume.</summary>
    Incomplete = 4,

    /// <summary>A ledger failed verification. Nothing was re-anchored over it and the rotation stopped.</summary>
    LedgerBroken = 3,

    /// <summary>The rotation could not run, or rows could not be re-keyed (see the log). Safe to fix and resume.</summary>
    Failed = 1,
}

public sealed record BrokenLedger(Guid LicenseId, string Reason);

public sealed class RotationReport
{
    public bool DryRun { get; init; }

    public required string CurrentKeyId { get; init; }

    public RotationOutcome Outcome { get; set; } = RotationOutcome.Complete;

    public string? Message { get; set; }

    public KeyUsageReport? Before { get; set; }

    public KeyUsageReport? After { get; set; }

    public long ClientKeysRewrapped { get; set; }

    public long MfaSecretsReencrypted { get; set; }

    public long LicensesReanchored { get; set; }

    public long CheckpointsResigned { get; set; }

    /// <summary>Rows another writer changed under us (re-checked, not an error).</summary>
    public long Contended { get; set; }

    /// <summary>Rows that could not be decrypted with the key they name. Left untouched; counted here so the operator investigates.</summary>
    public long UndecryptableRows { get; set; }

    public int BatchesRun { get; set; }

    public List<BrokenLedger> BrokenLedgers { get; } = [];
}

/// <summary>
/// Moves everything protected by a master key onto <c>Encryption:CurrentKeyId</c>, online. Every step is a short, conditional,
/// restartable operation: the row is only changed if it still holds the exact value that was read (so a concurrent writer or a second
/// runner makes it a no-op, never a double write), and a reader always sees a self-consistent row (old key + old value, or new key +
/// new value). Ledger checkpoints are only re-signed after the license's whole chain verified clean.
/// </summary>
public sealed partial class MasterKeyRotationService
{
    private const int MaxIdleRounds = 5;
    private const int MaxTransientRetries = 3;

    private readonly IServiceScopeFactory _scopes;
    private readonly MasterKeyProvider _master;
    private readonly PlatformCrypto _crypto;
    private readonly KeyUsageSurvey _survey;
    private readonly LedgerVerificationOptions _ledgerOptions;
    private readonly TimeProvider _time;
    private readonly ILogger<MasterKeyRotationService> _logger;

    public MasterKeyRotationService(
        IServiceScopeFactory scopes, MasterKeyProvider master, PlatformCrypto crypto, KeyUsageSurvey survey,
        IOptions<LedgerVerificationOptions> ledgerOptions, TimeProvider time, ILogger<MasterKeyRotationService> logger)
    {
        _scopes = scopes;
        _master = master;
        _crypto = crypto;
        _survey = survey;
        _ledgerOptions = ledgerOptions.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<RotationReport> RunAsync(RotationRequest request, CancellationToken cancellationToken)
    {
        var report = new RotationReport { DryRun = request.DryRun, CurrentKeyId = _master.CurrentKeyId };
        if (_master.IsEphemeral)
        {
            return Fail(report, "The keyring is ephemeral (Encryption:AllowEphemeralKey): a rotation needs the real keys. Supply Encryption:Keys.");
        }

        if (request.BatchSize is < 1 or > RotationRequest.MaxBatchSize)
        {
            return Fail(report, $"--batch-size must be between 1 and {RotationRequest.MaxBatchSize}.");
        }

        report.Before = await _survey.SurveyAsync(cancellationToken);
        if (report.Before.UnknownKeyIds.Count > 0)
        {
            return Fail(report, $"Live data references master key id(s) that are not in the keyring: {string.Join(", ", report.Before.UnknownKeyIds)}. Add those keys to Encryption:Keys first; nothing was changed.");
        }

        if (request.DryRun)
        {
            report.After = report.Before;
            report.Message = "Dry run: nothing was changed.";
            report.Outcome = report.Before.PendingTotal == 0 ? RotationOutcome.Complete : RotationOutcome.Incomplete;
            return report;
        }

        LogStarted(_logger, _master.CurrentKeyId, request.Resume, report.Before.PendingTotal);
        var budget = new Budget(request.MaxBatches);
        try
        {
            // 1. Ledgers first: a broken chain stops the rotation before any other data is touched.
            if (request.IncludeLedger && report.Before.Pending(KeyArtifacts.LedgerCheckpoint) > 0)
            {
                await ReanchorLedgersAsync(request, report, budget, cancellationToken);
                if (report.BrokenLedgers.Count > 0)
                {
                    report.Outcome = RotationOutcome.LedgerBroken;
                    report.Message = $"{report.BrokenLedgers.Count} ledger(s) failed verification: nothing was anchored over them and the rotation stopped before re-keying other data.";
                    LogLedgerBroken(_logger, report.BrokenLedgers.Count);
                    await AuditAsync(report, "keyrotation.aborted_ledger_broken", cancellationToken);
                    report.After = await _survey.SurveyAsync(cancellationToken);
                    return report;
                }
            }

            await RewrapClientKeysAsync(request, report, budget, cancellationToken);
            await ReencryptMfaAsync(request, report, budget, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogRotationError(_logger, ex.GetType().Name);
            report.After = await TrySurveyAsync(CancellationToken.None);
            return Fail(report, "The rotation stopped on an unexpected error (see the log). Fix the cause and run again with --resume.");
        }

        report.After = await _survey.SurveyAsync(cancellationToken);
        var pending = KeyArtifacts.All.Where(a => request.IncludeLedger || a != KeyArtifacts.LedgerCheckpoint).Sum(report.After.Pending);
        if (report.UndecryptableRows > 0)
        {
            report.Outcome = RotationOutcome.Failed;
            report.Message = $"{report.UndecryptableRows} row(s) could not be decrypted with the key they name and were left unchanged.";
        }
        else if (pending > 0)
        {
            report.Outcome = RotationOutcome.Incomplete;
            report.Message = $"{pending} artifact(s) are still under an older key (batch budget reached or rows kept changing). Run again with --resume.";
        }
        else
        {
            report.Outcome = RotationOutcome.Complete;
        }

        LogFinished(_logger, _master.CurrentKeyId, report.Outcome.ToString(), report.ClientKeysRewrapped, report.MfaSecretsReencrypted, report.CheckpointsResigned, report.PercentCompleteOrDefault());
        await AuditAsync(report, "keyrotation.finished", cancellationToken);
        return report;
    }

    /// <summary>
    /// The lazy path (<c>Encryption:LazyRewrap</c>): one small batch of client data keys plus a sweep of the MFA secrets, no ledger work
    /// (re-anchoring needs a full chain verification and stays with the explicit command). Cheap to call repeatedly; does nothing once
    /// everything is under the current key. Never throws for row-level problems.
    /// </summary>
    public async Task<RotationReport> RewrapSomeAsync(int batchSize, CancellationToken cancellationToken)
    {
        var report = new RotationReport { CurrentKeyId = _master.CurrentKeyId };
        if (_master.IsEphemeral)
        {
            return report;
        }

        var request = new RotationRequest(false, Math.Clamp(batchSize, 1, RotationRequest.MaxBatchSize), IncludeLedger: false);
        await RewrapClientKeysAsync(request, report, new Budget(1), cancellationToken);
        await ReencryptMfaAsync(request, report, new Budget(null), cancellationToken);
        if (report.ClientKeysRewrapped + report.MfaSecretsReencrypted > 0)
        {
            LogLazyBatch(_logger, report.CurrentKeyId, report.ClientKeysRewrapped, report.MfaSecretsReencrypted);
        }

        return report;
    }

    // ------------------------------------------------------------------ client data keys

    private async Task RewrapClientKeysAsync(RotationRequest request, RotationReport report, Budget budget, CancellationToken cancellationToken)
    {
        var current = _master.CurrentKeyId;
        var failed = new HashSet<Guid>();
        var idle = 0;
        while (budget.TryTake())
        {
            await using var scope = _scopes.CreateAsyncScope();
            using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("master key rotation: client data keys");
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var failedIds = failed.ToList();
            var rows = await db.ClientKeys.AsNoTracking()
                .Where(k => k.MasterKeyId != current && k.Status != ClientKeyStatus.Destroyed && !failedIds.Contains(k.Id))
                .OrderBy(k => k.Id).Take(request.BatchSize)
                .Select(k => new { k.Id, k.ClientId, k.KeyVersion, k.MasterKeyId, k.WrappedDataKey })
                .ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                return;
            }

            report.BatchesRun++;
            var progressed = 0;
            foreach (var row in rows)
            {
                byte[]? dataKey = null;
                try
                {
                    dataKey = DataKeyWrapper.Unwrap(_master, row.MasterKeyId, row.ClientId, row.KeyVersion, row.WrappedDataKey);
                    var rewrapped = DataKeyWrapper.Wrap(_master, current, row.ClientId, row.KeyVersion, dataKey);
                    var check = DataKeyWrapper.Unwrap(_master, current, row.ClientId, row.KeyVersion, rewrapped);
                    var same = CryptographicOperations.FixedTimeEquals(check, dataKey);
                    CryptographicOperations.ZeroMemory(check);
                    if (!same)
                    {
                        throw new CryptographicException("Re-wrapped key did not round-trip.");
                    }

                    // Exactly-once: only if the row still holds exactly what we read. A concurrent re-wrap, destroy or second runner changes it.
                    var updated = await db.ClientKeys
                        .Where(k => k.Id == row.Id && k.MasterKeyId == row.MasterKeyId && k.WrappedDataKey == row.WrappedDataKey && k.Status != ClientKeyStatus.Destroyed)
                        .ExecuteUpdateAsync(s => s.SetProperty(k => k.WrappedDataKey, rewrapped).SetProperty(k => k.MasterKeyId, current), cancellationToken);
                    if (updated == 1)
                    {
                        report.ClientKeysRewrapped++;
                        progressed++;
                    }
                    else
                    {
                        report.Contended++;
                    }
                }
                catch (CryptographicException ex)
                {
                    failed.Add(row.Id);
                    report.UndecryptableRows++;
                    LogRowFailure(_logger, KeyArtifacts.ClientDataKey, row.Id, row.MasterKeyId, ex.GetType().Name);
                }
                finally
                {
                    if (dataKey is not null)
                    {
                        CryptographicOperations.ZeroMemory(dataKey);
                    }
                }
            }

            idle = progressed == 0 && failed.Count == 0 ? idle + 1 : progressed == 0 ? idle : 0;
            if (idle >= MaxIdleRounds)
            {
                return; // contention that never settles: report as incomplete
            }
        }
    }

    // ------------------------------------------------------------------ MFA secrets

    private async Task ReencryptMfaAsync(RotationRequest request, RotationReport report, Budget budget, CancellationToken cancellationToken)
    {
        var current = _master.CurrentKeyId;
        for (var sweep = 0; sweep < MaxIdleRounds; sweep++)
        {
            Guid? after = null;
            var contended = 0;
            while (true)
            {
                if (!budget.TryTake())
                {
                    return;
                }

                await using var scope = _scopes.CreateAsyncScope();
                using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("master key rotation: MFA secrets");
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var query = db.UserMfa.AsNoTracking().AsQueryable();
                if (after is { } last)
                {
                    query = query.Where(m => m.Id.CompareTo(last) > 0);
                }

                var page = await query.OrderBy(m => m.Id).Take(request.BatchSize).Select(m => new { m.Id, m.UserId, m.SecretEnc }).ToListAsync(cancellationToken);
                report.BatchesRun++;
                foreach (var row in page.Where(r => _crypto.KeyIdOf(r.SecretEnc) != current))
                {
                    try
                    {
                        var sealedAgain = _crypto.Reprotect(row.SecretEnc, CryptoPurposes.MfaSecret, CryptoPurposes.MfaSecretContext(row.UserId));
                        var updated = await db.UserMfa
                            .Where(m => m.Id == row.Id && m.SecretEnc == row.SecretEnc)
                            .ExecuteUpdateAsync(s => s.SetProperty(m => m.SecretEnc, sealedAgain), cancellationToken);
                        if (updated == 1)
                        {
                            report.MfaSecretsReencrypted++;
                        }
                        else
                        {
                            contended++;
                            report.Contended++;
                        }
                    }
                    catch (CryptographicException ex)
                    {
                        report.UndecryptableRows++;
                        LogRowFailure(_logger, KeyArtifacts.MfaSecret, row.Id, _crypto.KeyIdOf(row.SecretEnc), ex.GetType().Name);
                    }
                }

                if (page.Count < request.BatchSize)
                {
                    break;
                }

                after = page[^1].Id;
            }

            if (contended == 0)
            {
                return;
            }
        }
    }

    // ------------------------------------------------------------------ ledger checkpoints

    private async Task ReanchorLedgersAsync(RotationRequest request, RotationReport report, Budget budget, CancellationToken cancellationToken)
    {
        var current = _master.CurrentKeyId;
        for (var round = 0; round < MaxIdleRounds; round++)
        {
            Guid? after = null;
            var contended = 0;
            while (true)
            {
                if (!budget.TryTake())
                {
                    return;
                }

                List<Guid> licenses;
                await using (var scope = _scopes.CreateAsyncScope())
                {
                    using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("master key rotation: find ledgers");
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var query = db.LedgerCheckpoints.AsNoTracking()
                        .Where(c => c.KeyId != current && !db.LedgerCheckpointSupersessions.Any(s => s.CheckpointId == c.Id));
                    if (after is { } last)
                    {
                        query = query.Where(c => c.LicenseId.CompareTo(last) > 0);
                    }

                    licenses = await query.Select(c => c.LicenseId).Distinct().OrderBy(id => id).Take(request.BatchSize).ToListAsync(cancellationToken);
                }

                if (licenses.Count == 0)
                {
                    break;
                }

                report.BatchesRun++;
                foreach (var licenseId in licenses)
                {
                    var (state, detail) = await ReanchorLicenseAsync(licenseId, report, cancellationToken);
                    switch (state)
                    {
                        case LicenseState.Broken:
                            report.BrokenLedgers.Add(new BrokenLedger(licenseId, detail ?? "failed verification"));
                            break;
                        case LicenseState.Contended:
                            contended++;
                            report.Contended++;
                            break;
                        case LicenseState.Anchored:
                            report.LicensesReanchored++;
                            break;
                    }
                }

                if (report.BrokenLedgers.Count > 0)
                {
                    return; // abort: never carry on re-keying around a broken chain
                }

                after = licenses[^1];
            }

            if (contended == 0)
            {
                return;
            }

            // Retry the licenses that changed under us (a nightly run anchored the same head, or two runners overlapped).
        }
    }

    private enum LicenseState
    {
        Anchored,
        Broken,
        Contended,
    }

    private async Task<(LicenseState State, string? Detail)> ReanchorLicenseAsync(Guid licenseId, RotationReport report, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("master key rotation: re-anchor ledger");
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<ILedgerVerificationStore>();
        var anchors = sp.GetRequiredService<LedgerAnchorService>();
        var db = sp.GetRequiredService<AppDbContext>();
        var current = _master.CurrentKeyId;

        var head = await store.GetHeadAsync(licenseId, cancellationToken);
        if (head is null)
        {
            return (LicenseState.Broken, "the license no longer exists but its signed checkpoints do (it was deleted with its ledger)");
        }

        // 1. Verify the whole chain against every checkpoint under the key that signed it (the OLD key for the old ones).
        var (authentic, forged) = await anchors.LoadAsync(licenseId, cancellationToken);
        if (forged.Count > 0)
        {
            return (LicenseState.Broken, forged[0].Message);
        }

        LedgerChainVerifier verifier;
        var attempt = 0;
        while (true)
        {
            var problems = new List<LedgerProblem>();
            verifier = new LedgerChainVerifier(authentic);
            long afterEntry = 0;
            while (true)
            {
                var batch = await store.ListEntriesAsync(licenseId, afterEntry, _ledgerOptions.EntryBatchSize, cancellationToken);
                foreach (var entry in batch)
                {
                    verifier.Accept(entry, problems);
                }

                if (batch.Count < _ledgerOptions.EntryBatchSize)
                {
                    break;
                }

                afterEntry = batch[^1].Id;
            }

            var latest = await store.GetHeadAsync(licenseId, cancellationToken) ?? head;
            verifier.Finish(latest.Remaining, problems);
            if (problems.Count == 0)
            {
                break;
            }

            if (problems.Any(p => !p.IsTransient))
            {
                return (LicenseState.Broken, problems.First(p => !p.IsTransient).Message);
            }

            if (attempt++ >= MaxTransientRetries)
            {
                return (LicenseState.Broken, problems[0].Message);
            }

            await Task.Delay(Math.Max(50, _ledgerOptions.RecheckDelayMilliseconds), cancellationToken); // a charge may have landed between reading rows and the balance
        }

        // 2. Chain is clean: re-sign every checkpoint under the current key, check the copies, then supersede the originals - one transaction.
        var old = authentic.Where(c => c.KeyId != current).ToList();
        if (old.Count == 0)
        {
            return (LicenseState.Anchored, null);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var replacements = new Dictionary<long, LedgerCheckpoint>(); // by LastEntryId: two old keys may both have anchored the same head
            var existing = authentic.Where(c => c.KeyId == current).ToDictionary(c => c.LastEntryId);
            foreach (var checkpoint in old)
            {
                if (!existing.ContainsKey(checkpoint.LastEntryId) && !replacements.ContainsKey(checkpoint.LastEntryId))
                {
                    var copy = anchors.Resign(checkpoint);
                    db.LedgerCheckpoints.Add(copy);
                    replacements[checkpoint.LastEntryId] = copy;
                }
            }

            // Anchor the head we just proved clean under the new key too, unless a checkpoint of the current key already covers it.
            if (verifier.Entries > 0 && !existing.ContainsKey(verifier.LastEntryId) && !replacements.ContainsKey(verifier.LastEntryId))
            {
                db.LedgerCheckpoints.Add(anchors.Create(licenseId, head.ClientId, verifier));
            }

            await db.SaveChangesAsync(cancellationToken);

            foreach (var checkpoint in old)
            {
                var verified = replacements.TryGetValue(checkpoint.LastEntryId, out var added) ? added : existing[checkpoint.LastEntryId];
                var replacementId = verified.Id;
                if (!anchors.IsAuthentic(verified) || verified.HeadHash.AsSpan().SequenceEqual(checkpoint.HeadHash) is false
                    || verified.EntryCount != checkpoint.EntryCount || verified.BalanceAfter != checkpoint.BalanceAfter || verified.KeyId != current)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return (LicenseState.Broken, "a re-signed checkpoint did not verify against its original; nothing was superseded");
                }

                db.LedgerCheckpointSupersessions.Add(LedgerCheckpointSupersession.Create(checkpoint, replacementId, _time.GetUtcNow().UtcDateTime));
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            report.CheckpointsResigned += old.Count;
            LogLicenseAnchored(_logger, licenseId, old.Count, current);
            return (LicenseState.Anchored, null);
        }
        catch (DbUpdateException)
        {
            // Unique (license, entry, key) or supersession already present: another runner / the nightly job got there first. Re-evaluate on the next pass.
            await transaction.RollbackAsync(CancellationToken.None);
            return (LicenseState.Contended, null);
        }
    }

    // ------------------------------------------------------------------ helpers

    private RotationReport Fail(RotationReport report, string message)
    {
        report.Outcome = RotationOutcome.Failed;
        report.Message = message;
        LogRotationRefused(_logger, message);
        return report;
    }

    private async Task<KeyUsageReport?> TrySurveyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _survey.SurveyAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task AuditAsync(RotationReport report, string action, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("master key rotation: audit");
        scope.ServiceProvider.GetRequiredService<IAuditService>().Record(new AuditEntry(
            action, "MasterKey", report.CurrentKeyId, null,
            NewValues: new
            {
                report.CurrentKeyId,
                Outcome = report.Outcome.ToString(),
                report.ClientKeysRewrapped,
                report.MfaSecretsReencrypted,
                report.LicensesReanchored,
                report.CheckpointsResigned,
                report.Contended,
                report.UndecryptableRows,
                BrokenLedgers = report.BrokenLedgers.Count,
            }));
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(cancellationToken);
    }

    private sealed class Budget
    {
        private int? _remaining;

        public Budget(int? max)
        {
            _remaining = max;
        }

        public bool TryTake()
        {
            if (_remaining is null)
            {
                return true;
            }

            if (_remaining <= 0)
            {
                return false;
            }

            _remaining--;
            return true;
        }
    }

    [LoggerMessage(EventId = 7100, Level = LogLevel.Warning, Message = "Master key rotation started: target key {KeyId}, resume {Resume}, {Pending} artifact(s) under an older key")]
    private static partial void LogStarted(ILogger logger, string keyId, bool resume, long pending);

    [LoggerMessage(EventId = 7101, Level = LogLevel.Warning,
        Message = "Master key rotation {Outcome} (target {KeyId}): {ClientKeys} client key(s) re-wrapped, {Mfa} MFA secret(s) re-encrypted, {Checkpoints} checkpoint(s) re-signed, {Percent}% complete")]
    private static partial void LogFinished(ILogger logger, string keyId, string outcome, long clientKeys, long mfa, long checkpoints, double percent);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Critical, Message = "MASTER KEY ROTATION ABORTED: {Count} ledger(s) failed verification. Checkpoints were NOT re-signed over them.")]
    private static partial void LogLedgerBroken(ILogger logger, int count);

    [LoggerMessage(EventId = 7103, Level = LogLevel.Error, Message = "Master key rotation: {Artifact} row {RowId} (protected by key '{KeyId}') could not be decrypted ({Reason}); left unchanged")]
    private static partial void LogRowFailure(ILogger logger, string artifact, Guid rowId, string keyId, string reason);

    [LoggerMessage(EventId = 7104, Level = LogLevel.Information, Message = "Master key rotation: license {LicenseId} re-anchored ({Count} checkpoint(s) re-signed under key {KeyId})")]
    private static partial void LogLicenseAnchored(ILogger logger, Guid licenseId, int count, string keyId);

    [LoggerMessage(EventId = 7107, Level = LogLevel.Information, Message = "Lazy re-wrap to master key {KeyId}: {ClientKeys} client key(s), {Mfa} MFA secret(s)")]
    private static partial void LogLazyBatch(ILogger logger, string keyId, long clientKeys, long mfa);

    [LoggerMessage(EventId = 7105, Level = LogLevel.Error, Message = "Master key rotation refused: {Reason}")]
    private static partial void LogRotationRefused(ILogger logger, string reason);

    [LoggerMessage(EventId = 7106, Level = LogLevel.Error, Message = "Master key rotation stopped on an unexpected {ErrorType}; rerun with --resume")]
    private static partial void LogRotationError(ILogger logger, string errorType);
}

internal static class RotationReportExtensions
{
    public static double PercentCompleteOrDefault(this RotationReport report) => report.After?.PercentComplete ?? 0;
}
