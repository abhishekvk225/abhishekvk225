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

        public Func<int, int>? RemainingOverride { get; set; }

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
            var head = Licenses.Single(l => l.Head.LicenseId == licenseId).Head;
            return Task.FromResult<LicenseLedgerHead?>(RemainingOverride is { } f ? head with { Remaining = f(HeadReads) } : head);
        }

        public Task<IReadOnlyList<LicenseTransaction>> ListEntriesAsync(Guid licenseId, long afterEntryId, int take, CancellationToken cancellationToken)
        {
            var entries = Licenses.Single(l => l.Head.LicenseId == licenseId).Entries;
            return Task.FromResult<IReadOnlyList<LicenseTransaction>>(entries.Where(e => e.Id > afterEntryId).OrderBy(e => e.Id).Take(take).ToList());
        }
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

        public LedgerVerificationService Service(LedgerVerificationGate? gate = null) =>
            new(Store, gate ?? new LedgerVerificationGate(), Audit, Uow, Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System, Log);

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

        var report = (await h.Service().VerifyAllAsync(default)).Value;

        report.LicensesChecked.ShouldBe(25);
        report.EntriesChecked.ShouldBe(25 * 3);
        report.BrokenLicenses.ShouldBe(0);
        h.Audit.Entries.ShouldBeEmpty();
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

        var report = (await h.Service().VerifyAllAsync(default)).Value;

        report.BrokenLicenses.ShouldBe(1);
        var broken = report.Breaks.Single();
        broken.LicenseId.ShouldBe(bad.Id);
        broken.ClientId.ShouldBe(bad.ClientId);
        broken.FirstBrokenEntryId.ShouldBe(badEntries[3].Id);
        h.Log.Entries.ShouldContain(e => e.Level == LogLevel.Critical && e.Message.Contains(bad.Id.ToString()));
        var audit = h.Audit.Entries.Single();
        audit.Action.ShouldBe("ledger.verification_failed");
        audit.EntityId.ShouldBe(bad.Id.ToString());
        audit.ClientId.ShouldBe(bad.ClientId);
        h.Uow.Saves.ShouldBe(1);
    }

    [Fact]
    public async Task A_charge_that_lands_while_verifying_is_rechecked_instead_of_reported()
    {
        var h = new Harness();
        var (license, entries) = Chain();
        h.Add(license, entries);
        // First read of the license balance is stale (a charge committed after the ledger was read); the re-check sees the truth.
        h.Store.RemainingOverride = reads => reads == 1 ? license.Remaining - 1 : license.Remaining;

        var report = (await h.Service().VerifyAllAsync(default)).Value;

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

        var report = (await h.Service().VerifyAllAsync(default)).Value;

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

        var result = await h.Service(gate).VerifyAllAsync(default);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Type.ShouldBe(Common.ErrorType.Conflict);
        gate.Exit();
        (await h.Service(gate).VerifyAllAsync(default)).IsSuccess.ShouldBeTrue(); // released after the earlier refusal and after a normal run
    }
}
