using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Licensing;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.UnitTests;

/// <summary>Builds real hash-chained ledgers in memory and then tampers with them the way raw database access could.</summary>
public class LedgerVerificationTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static (License License, List<LicenseTransaction> Entries) Chain(int consumes = 4, int grant = 100)
    {
        var license = License.Create(Guid.NewGuid(), "Chain", null, "NXV-TEST", grant, T0.AddDays(-1), T0.AddDays(60));
        var entries = new List<LicenseTransaction>();
        var balance = 0;
        byte[]? previous = null;

        void Append(LedgerEntryType type, int credits)
        {
            var e = LicenseTransaction.Create(license, type, credits, balance, type == LedgerEntryType.Consume ? MeteredOperation.Verify : null, null, null, null, "test", "System", null, null, previous, T0.AddMinutes(entries.Count));
            typeof(LicenseTransaction).GetProperty(nameof(LicenseTransaction.Id))!.SetValue(e, (long)entries.Count + 1); // the database assigns ids; in memory we do
            entries.Add(e);
            balance = e.BalanceAfter;
            previous = e.RowHash;
        }

        Append(LedgerEntryType.Grant, grant);
        for (var i = 0; i < consumes; i++)
        {
            Append(LedgerEntryType.Consume, -1);
        }

        // Keep the license aggregate in step with the ledger (Consume rows move ConsumedCredits).
        typeof(License).GetProperty(nameof(License.ConsumedCredits))!.SetValue(license, consumes);
        return (license, entries);
    }

    private static void Tamper(LicenseTransaction entry, string property, object value) =>
        typeof(LicenseTransaction).GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.SetValue(entry, value);

    [Fact]
    public void An_untouched_chain_verifies()
    {
        var (license, entries) = Chain();

        LedgerVerifier.Analyse(license.Remaining, entries).ShouldBeEmpty();
        LedgerVerifier.Verify(license, entries).Valid.ShouldBeTrue();
    }

    [Fact]
    public void An_edited_amount_is_pinned_to_the_row_that_was_edited()
    {
        var (license, entries) = Chain();
        Tamper(entries[2], nameof(LicenseTransaction.Credits), -50);

        var problems = LedgerVerifier.Analyse(license.Remaining, entries);

        problems.Select(p => p.EntryId).Where(id => id is not null).Distinct().ShouldBe([entries[2].Id]);
        problems.ShouldContain(p => p.Message.Contains("does not match its hash"));
    }

    [Fact]
    public void A_rewritten_row_with_a_recomputed_hash_breaks_the_link_to_the_next_row()
    {
        var (license, entries) = Chain();
        Tamper(entries[2], nameof(LicenseTransaction.Reason), "forged");
        Tamper(entries[2], nameof(LicenseTransaction.RowHash), entries[2].ComputeHash()); // a careful attacker fixes the row's own hash...

        var problems = LedgerVerifier.Analyse(license.Remaining, entries);

        // ...but the next row still points at the old hash
        problems.Single(p => p.EntryId is not null).Message.ShouldContain("chain broken");
    }

    [Fact]
    public void A_deleted_row_breaks_the_chain_and_the_balance()
    {
        var (license, entries) = Chain();
        entries.RemoveAt(2);

        var problems = LedgerVerifier.Analyse(license.Remaining, entries);

        problems.ShouldNotBeEmpty();
        problems.ShouldContain(p => p.Message.Contains("chain broken"));
    }

    [Fact]
    public void A_license_balance_edited_behind_the_ledgers_back_is_reported_without_a_row()
    {
        var (license, entries) = Chain();

        var problems = LedgerVerifier.Analyse(license.Remaining + 10, entries);

        var problem = problems.ShouldHaveSingleItem();
        problem.EntryId.ShouldBeNull();
        problem.Message.ShouldContain("differs from the license's remaining credits");
    }

    [Fact]
    public void Verifying_in_batches_gives_the_same_answer_as_verifying_at_once()
    {
        var (license, entries) = Chain(consumes: 9);
        Tamper(entries[7], nameof(LicenseTransaction.BalanceAfter), 1);

        var verifier = new LedgerChainVerifier();
        var batched = new List<LedgerProblem>();
        foreach (var batch in entries.Chunk(3))
        {
            foreach (var e in batch)
            {
                verifier.Accept(e, batched);
            }
        }

        verifier.Finish(license.Remaining, batched);

        batched.ShouldBe(LedgerVerifier.Analyse(license.Remaining, entries));
        verifier.Entries.ShouldBe(entries.Count);
    }

    // ---- the platform-wide service, against an in-memory store ----

    private sealed class FakeStore : ILedgerVerificationStore
    {
        public List<(LicenseLedgerHead Head, List<LicenseTransaction> Entries)> Licenses { get; } = [];

        public int HeadReads { get; private set; }

        public Guid? Poisoned { get; set; }

        public Func<int, int>? RemainingOverride { get; set; }

        public List<LedgerCheckpoint> Checkpoints { get; } = [];

        public List<LedgerBreakRecord> Breaks { get; } = [];

        public List<LedgerVerificationRun> Runs { get; } = [];

        public Task<IReadOnlyList<LedgerCheckpoint>> ListCheckpointsAsync(Guid licenseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LedgerCheckpoint>>(Checkpoints.Where(c => c.LicenseId == licenseId).OrderBy(c => c.LastEntryId).ToList());

        public Task<IReadOnlyList<LicenseLedgerHead>> ListOrphanCheckpointLicensesAsync(CancellationToken cancellationToken)
        {
            var known = Licenses.Select(l => l.Head.LicenseId).ToHashSet();
            return Task.FromResult<IReadOnlyList<LicenseLedgerHead>>(
                Checkpoints.Where(c => !known.Contains(c.LicenseId)).Select(c => new LicenseLedgerHead(c.LicenseId, c.ClientId, 0)).DistinctBy(h => h.LicenseId).ToList());
        }

        public void Add(LedgerCheckpoint checkpoint) => Checkpoints.Add(checkpoint);

        public Task<IReadOnlyList<LedgerBreakRecord>> ListOpenBreaksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LedgerBreakRecord>>(Breaks.Where(b => b.ClearedAt is null).ToList());

        public void Add(LedgerBreakRecord record) => Breaks.Add(record);

        public void Add(LedgerVerificationRun run) => Runs.Add(run);

        public Task<LedgerVerificationRun?> GetRunAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Runs.FirstOrDefault(r => r.Id == id));

        public Task<IReadOnlyList<LedgerVerificationRun>> ListRunningAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LedgerVerificationRun>>(Runs.Where(r => r.Status == LedgerRunStatus.Running).ToList());

        public Task<IReadOnlyList<LedgerVerificationRun>> ListRecentRunsAsync(int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LedgerVerificationRun>>(Runs.OrderByDescending(r => r.StartedAt).Take(take).ToList());

        public Task<IReadOnlyList<LicenseLedgerHead>> ListLicensesAsync(Guid? afterLicenseId, int take, CancellationToken cancellationToken)
        {
            var ordered = Licenses.Select(l => l.Head).OrderBy(h => h.LicenseId).AsEnumerable();
            if (afterLicenseId is { } after)
            {
                ordered = ordered.Where(h => h.LicenseId.CompareTo(after) > 0);
            }

            return Task.FromResult<IReadOnlyList<LicenseLedgerHead>>(ordered.Take(take).ToList());
        }

        public Task<LicenseLedgerHead?> GetHeadAsync(Guid licenseId, CancellationToken cancellationToken)
        {
            HeadReads++;
            if (Licenses.Where(l => l.Head.LicenseId == licenseId).Select(l => l.Head).SingleOrDefault() is not { } head)
            {
                return Task.FromResult<LicenseLedgerHead?>(null);
            }

            return Task.FromResult<LicenseLedgerHead?>(RemainingOverride is { } f ? head with { Remaining = f(HeadReads) } : head);
        }

        public Task<IReadOnlyList<LicenseTransaction>> ListEntriesAsync(Guid licenseId, long afterEntryId, int take, CancellationToken cancellationToken)
        {
            if (licenseId == Poisoned)
            {
                throw new InvalidOperationException("unreadable");
            }

            var entries = Licenses.Single(l => l.Head.LicenseId == licenseId).Entries;
            return Task.FromResult<IReadOnlyList<LicenseTransaction>>(entries.Where(e => e.Id > afterEntryId).OrderBy(e => e.Id).Take(take).ToList());
        }
    }

    private sealed class NoUser : ICurrentUser
    {
        public bool IsAuthenticated => false;

        public ActorType ActorType => ActorType.Anonymous;

        public Guid? ActorId => null;

        public Guid? ClientId => null;

        public bool IsPlatformUser => false;

        public IReadOnlyCollection<string> Roles => [];
    }

    private sealed class FakeLauncher : ILedgerRunLauncher
    {
        public List<(Guid RunId, Guid? LicenseId, IAsyncDisposable Lease)> Launched { get; } = [];

        public void Launch(Guid runId, Guid? licenseId, IAsyncDisposable lease) => Launched.Add((runId, licenseId, lease));
    }

    /// <summary>Stands in for the master-key based crypto: a fixed key HMAC is all the anchor needs.</summary>
    private sealed class TestCrypto : IPlatformCrypto
    {
        public byte[] Key { get; set; } = System.Text.Encoding.UTF8.GetBytes("unit-test-ledger-anchor-key-000001");

        public string KeyId => "test";

        public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose, string context) => throw new NotSupportedException();

        public byte[] Unprotect(ReadOnlySpan<byte> payload, string purpose, string context) => throw new NotSupportedException();

        public byte[] ComputeMac(string purpose, ReadOnlySpan<byte> data) => System.Security.Cryptography.HMACSHA256.HashData(Key, data);
    }

    private sealed class RecordingAudit : IAuditService
    {
        public List<AuditEntry> Entries { get; } = [];

        public void Record(AuditEntry entry) => Entries.Add(entry);
    }

    private sealed class CountingUnitOfWork : IUnitOfWork
    {
        public int Saves { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Saves++;
            return Task.FromResult(0);
        }

        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default) => action(cancellationToken);

        public void ClearTracked()
        {
        }
    }

    private sealed class CapturingLogger : ILogger<LedgerVerificationService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class Harness
    {
        public FakeStore Store { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public CountingUnitOfWork Uow { get; } = new();

        public CapturingLogger Log { get; } = new();

        public LedgerVerificationOptions Options { get; } = new() { LicenseBatchSize = 10, EntryBatchSize = 100, RecheckDelayMilliseconds = 0, BalanceRecheckAttempts = 2 };

        public FakeLauncher Launcher { get; } = new();

        public TestCrypto Crypto { get; } = new();

        public LedgerVerificationService Service(LedgerVerificationGate? gate = null, IDistributedLock? distributedLock = null) =>
            new(Store, new LedgerAnchorService(Crypto, Store, TimeProvider.System), gate ?? new LedgerVerificationGate(), Launcher, new NoUser(), Audit, Uow,
                Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System, Log, distributedLock);

        public Guid Add(License license, List<LicenseTransaction> entries)
        {
            Store.Licenses.Add((new LicenseLedgerHead(license.Id, license.ClientId, license.Remaining), entries));
            return license.Id;
        }
    }

    [Fact]
    public async Task A_healthy_platform_produces_a_clean_report_and_no_audit_noise()
    {
        var h = new Harness();
        for (var i = 0; i < 25; i++) // more licenses than one batch
        {
            var (license, entries) = Chain(consumes: 2);
            h.Add(license, entries);
        }

        var report = (await h.Service().RunAsync("test", null, default)).Value;

        report.LicensesChecked.ShouldBe(25);
        report.EntriesChecked.ShouldBe(25 * 3);
        report.BrokenLicenses.ShouldBe(0);
        h.Audit.Entries.ShouldBeEmpty();
        h.Store.Checkpoints.Count.ShouldBe(25); // every license that verified clean is anchored
        h.Log.Entries.ShouldNotContain(e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task A_tampered_row_is_reported_with_the_first_broken_entry_logged_critical_and_audited()
    {
        var h = new Harness();
        var (good, goodEntries) = Chain();
        var (bad, badEntries) = Chain();
        Tamper(badEntries[3], nameof(LicenseTransaction.Credits), -9);
        Tamper(badEntries[4], nameof(LicenseTransaction.Credits), -9);
        h.Add(good, goodEntries);
        h.Add(bad, badEntries);

        var report = (await h.Service().RunAsync("test", null, default)).Value;

        report.BrokenLicenses.ShouldBe(1);
        var broken = report.Breaks.Single();
        broken.LicenseId.ShouldBe(bad.Id);
        broken.ClientId.ShouldBe(bad.ClientId);
        broken.FirstBrokenEntryId.ShouldBe(badEntries[3].Id);
        h.Log.Entries.ShouldContain(e => e.Level == LogLevel.Critical && e.Message.Contains(bad.Id.ToString()));
        var audit = h.Audit.Entries.Single(e => e.Action == "ledger.verification_failed");
        audit.Action.ShouldBe("ledger.verification_failed");
        audit.EntityId.ShouldBe(bad.Id.ToString());
        audit.ClientId.ShouldBe(bad.ClientId);
        h.Uow.Saves.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_charge_that_lands_while_verifying_is_rechecked_instead_of_reported()
    {
        var h = new Harness();
        var (license, entries) = Chain();
        h.Add(license, entries);
        // First read of the license balance is stale (a charge committed after the ledger was read); the re-check sees the truth.
        h.Store.RemainingOverride = reads => reads == 1 ? license.Remaining - 1 : license.Remaining;

        var report = (await h.Service().RunAsync("test", null, default)).Value;

        report.BrokenLicenses.ShouldBe(0);
        h.Store.HeadReads.ShouldBe(2);
    }

    [Fact]
    public async Task A_balance_that_stays_wrong_is_reported_after_the_rechecks()
    {
        var h = new Harness();
        var (license, entries) = Chain();
        h.Add(license, entries);
        h.Store.RemainingOverride = _ => license.Remaining + 7;

        var report = (await h.Service().RunAsync("test", null, default)).Value;

        var broken = report.Breaks.Single();
        broken.FirstBrokenEntryId.ShouldBeNull();
        broken.Reason.ShouldContain("differs");
        h.Store.HeadReads.ShouldBe(1 + h.Options.BalanceRecheckAttempts);
    }

    [Fact]
    public async Task Only_one_verification_runs_at_a_time()
    {
        var h = new Harness();
        var gate = new LedgerVerificationGate();
        gate.TryEnter().ShouldBeTrue();

        var result = await h.Service(gate).RunAsync("test", null, default);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Type.ShouldBe(Common.ErrorType.Conflict);
        gate.Exit();
        (await h.Service(gate).RunAsync("test", null, default)).IsSuccess.ShouldBeTrue(); // released after the earlier refusal and after a normal run
    }

    private sealed class HeldElsewhere : IDistributedLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken cancellationToken) => Task.FromResult<IAsyncDisposable?>(null);
    }

    [Fact]
    public async Task A_verification_held_by_another_node_is_refused()
    {
        var h = new Harness();
        var result = await h.Service(distributedLock: new HeldElsewhere()).RunAsync("test", null, default);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Type.ShouldBe(Common.ErrorType.Conflict);
    }

    [Fact]
    public async Task An_unreadable_license_is_reported_without_hiding_the_others()
    {
        var h = new Harness();
        var (bad, badEntries) = Chain();
        var (good, goodEntries) = Chain();
        var badId = h.Add(bad, badEntries);
        h.Add(good, goodEntries);
        h.Store.Poisoned = badId;

        var report = (await h.Service().RunAsync("test", null, default)).Value;

        report.LicensesChecked.ShouldBe(2);
        report.Breaks.Single().LicenseId.ShouldBe(badId);
    }

    // ---- keyed checkpoints: what the unkeyed chain cannot catch ----

    /// <summary>The careful attacker: rewrites a row and recomputes every hash after it, and the license balance, so the plain chain is perfect.</summary>
    private static void RewriteAndRehash(License license, List<LicenseTransaction> entries, int index, string newReason)
    {
        Tamper(entries[index], nameof(LicenseTransaction.Reason), newReason);
        byte[] previous = index == 0 ? LicenseTransaction.Genesis : entries[index - 1].RowHash;
        for (var i = index; i < entries.Count; i++)
        {
            Tamper(entries[i], nameof(LicenseTransaction.PrevHash), previous);
            Tamper(entries[i], nameof(LicenseTransaction.RowHash), entries[i].ComputeHash());
            previous = entries[i].RowHash;
        }
    }

    [Fact]
    public async Task A_clean_run_anchors_the_head_once_and_again_only_when_the_ledger_grew()
    {
        var h = new Harness();
        var (license, entries) = Chain();
        h.Add(license, entries);

        await h.Service().RunAsync("test", null, default);
        await h.Service().RunAsync("test", null, default);

        var checkpoint = h.Store.Checkpoints.ShouldHaveSingleItem();
        checkpoint.LastEntryId.ShouldBe(entries[^1].Id);
        checkpoint.EntryCount.ShouldBe(entries.Count);
        checkpoint.HeadHash.ShouldBe(entries[^1].RowHash);
        checkpoint.KeyId.ShouldBe("test");
    }

    [Fact]
    public async Task A_rewrite_with_a_recomputed_unkeyed_chain_passes_the_plain_check_but_not_the_signed_checkpoint()
    {
        var h = new Harness();
        var (license, entries) = Chain();
        h.Add(license, entries);
        await h.Service().RunAsync("test", null, default); // anchors the honest head

        RewriteAndRehash(license, entries, 2, "forged but consistent");

        // The unkeyed check is fooled: chain, row hashes and balances are all internally consistent again.
        LedgerVerifier.Analyse(license.Remaining, entries).ShouldBeEmpty();

        var report = (await h.Service().RunAsync("test", null, default)).Value;

        var broken = report.Breaks.ShouldHaveSingleItem();
        broken.LicenseId.ShouldBe(license.Id);
        broken.FirstBrokenEntryId.ShouldBe(entries[^1].Id); // pinned to the row the checkpoint covered
        broken.Reason.ShouldContain("signed checkpoint");
        h.Store.Checkpoints.Count.ShouldBe(1); // a broken head is never anchored
    }

    [Fact]
    public async Task Deleting_the_newest_rows_is_detected_because_the_checkpoint_has_no_row_left()
    {
        var h = new Harness();
        var (license, entries) = Chain(consumes: 5);
        var id = h.Add(license, entries);
        await h.Service().RunAsync("test", null, default);

        // Truncate the tail and bring the license balance in line, as someone who controls the database could.
        var head = h.Store.Licenses.Single().Head;
        entries.RemoveRange(entries.Count - 2, 2);
        h.Store.Licenses[0] = (head with { Remaining = entries[^1].BalanceAfter }, entries);

        var report = (await h.Service().RunAsync("test", null, default)).Value;

        // the plain check cannot tell
        LedgerVerifier.Analyse(entries[^1].BalanceAfter, entries).ShouldBeEmpty();
        var broken = report.Breaks.ShouldHaveSingleItem();
        broken.LicenseId.ShouldBe(id);
        broken.FirstBrokenEntryId.ShouldBeNull();
        broken.Reason.ShouldContain("truncated");
    }

    [Fact]
    public async Task A_license_that_vanished_with_its_whole_ledger_is_reported_from_its_checkpoints()
    {
        var h = new Harness();
        var (license, entries) = Chain();
        h.Add(license, entries);
        await h.Service().RunAsync("test", null, default);

        h.Store.Licenses.Clear();
        var report = (await h.Service().RunAsync("test", null, default)).Value;

        var broken = report.Breaks.ShouldHaveSingleItem();
        broken.LicenseId.ShouldBe(license.Id);
        broken.Reason.ShouldContain("no longer exists");
    }

    [Fact]
    public async Task A_checkpoint_edited_in_the_database_fails_its_signature()
    {
        var h = new Harness();
        var (license, entries) = Chain();
        h.Add(license, entries);
        await h.Service().RunAsync("test", null, default);
        typeof(LedgerCheckpoint).GetProperty(nameof(LedgerCheckpoint.EntryCount))!.SetValue(h.Store.Checkpoints[0], 99L);

        var report = (await h.Service().RunAsync("test", null, default)).Value;

        report.Breaks.ShouldHaveSingleItem().Reason.ShouldContain("signature");
    }

    [Fact]
    public async Task A_different_anchor_key_cannot_forge_a_matching_checkpoint()
    {
        var h = new Harness();
        var (license, entries) = Chain();
        h.Add(license, entries);
        await h.Service().RunAsync("test", null, default);
        h.Crypto.Key = System.Text.Encoding.UTF8.GetBytes("an-attacker-without-the-real-key!!");

        var report = (await h.Service().RunAsync("test", null, default)).Value;

        report.BrokenLicenses.ShouldBe(1); // the stored MAC no longer verifies under the real key
    }

    // ---- alert de-duplication ----

    [Fact]
    public async Task A_persistent_break_is_audited_once_but_logged_critical_on_every_run()
    {
        var h = new Harness();
        var (bad, badEntries) = Chain();
        Tamper(badEntries[2], nameof(LicenseTransaction.Credits), -9);
        h.Add(bad, badEntries);

        for (var run = 0; run < 3; run++)
        {
            await h.Service().RunAsync("test", null, default);
        }

        h.Audit.Entries.Count(e => e.Action == "ledger.verification_failed").ShouldBe(1);
        h.Log.Entries.Count(e => e.Level == LogLevel.Critical).ShouldBe(3);
        var record = h.Store.Breaks.ShouldHaveSingleItem();
        record.TimesSeen.ShouldBe(3);
        record.BreakKey.ShouldBe($"row:{badEntries[2].Id}");
    }

    [Fact]
    public async Task A_new_break_on_another_row_is_audited_again_and_a_healed_license_closes_its_record()
    {
        var h = new Harness();
        var (license, entries) = Chain(consumes: 6);
        h.Add(license, entries);
        var original = entries[2].Credits;
        Tamper(entries[2], nameof(LicenseTransaction.Credits), -9);
        await h.Service().RunAsync("test", null, default);

        Tamper(entries[2], nameof(LicenseTransaction.Credits), original); // restored (e.g. from backup)
        await h.Service().RunAsync("test", null, default);
        h.Store.Breaks.Single().ClearedAt.ShouldNotBeNull();

        Tamper(entries[4], nameof(LicenseTransaction.Credits), -9);
        await h.Service().RunAsync("test", null, default);

        h.Audit.Entries.Count(e => e.Action == "ledger.verification_failed").ShouldBe(2);
    }

    // ---- the non-blocking start ----

    [Fact]
    public async Task Starting_a_check_returns_a_running_run_at_once_and_hands_the_lease_to_the_worker()
    {
        var h = new Harness();
        var (license, entries) = Chain();
        h.Add(license, entries);
        var gate = new LedgerVerificationGate();

        var started = await h.Service(gate).StartAsync(null, default);

        started.IsSuccess.ShouldBeTrue();
        started.Value.Status.ShouldBe("Running");
        var launched = h.Launcher.Launched.ShouldHaveSingleItem();
        launched.RunId.ShouldBe(started.Value.Id);
        gate.TryEnter().ShouldBeFalse(); // still held: the worker owns it

        await h.Service(gate).ExecuteRunAsync(launched.RunId, null, default);
        await launched.Lease.DisposeAsync(); // what the launcher does when the work ends
        gate.TryEnter().ShouldBeTrue();
        gate.Exit();

        var finished = (await h.Service(gate).GetRunAsync(started.Value.Id, default)).Value;
        finished.Status.ShouldBe("Completed");
        finished.LicensesChecked.ShouldBe(1);
    }

    [Fact]
    public async Task A_second_start_while_one_is_active_is_a_conflict()
    {
        var h = new Harness();
        var gate = new LedgerVerificationGate();
        (await h.Service(gate).StartAsync(null, default)).IsSuccess.ShouldBeTrue();

        var second = await h.Service(gate).StartAsync(null, default);

        second.IsFailure.ShouldBeTrue();
        second.Error!.Type.ShouldBe(Common.ErrorType.Conflict);
    }

    [Fact]
    public async Task A_run_for_one_license_checks_only_that_license()
    {
        var h = new Harness();
        var (a, aEntries) = Chain();
        var (b, bEntries) = Chain();
        h.Add(a, aEntries);
        h.Add(b, bEntries);

        var report = (await h.Service().RunAsync("test", b.Id, default)).Value;

        report.LicensesChecked.ShouldBe(1);
        h.Store.Checkpoints.ShouldHaveSingleItem().LicenseId.ShouldBe(b.Id);
    }

    [Fact]
    public async Task Starting_a_check_for_an_unknown_license_is_not_found()
    {
        var h = new Harness();
        h.Add(Chain().License, Chain().Entries);

        var result = await h.Service().StartAsync(Guid.NewGuid(), default);

        result.Error!.Type.ShouldBe(Common.ErrorType.NotFound);
    }

    [Fact]
    public async Task A_run_left_running_by_a_crashed_node_is_marked_interrupted_by_the_next_one()
    {
        var h = new Harness();
        var stale = LedgerVerificationRun.Start("manual", null, null, DateTime.UtcNow.AddHours(-3));
        h.Store.Add(stale);

        await h.Service().RunAsync("test", null, default);

        stale.Status.ShouldBe(LedgerRunStatus.Failed);
        stale.Error.ShouldNotBeNull();
    }
}
