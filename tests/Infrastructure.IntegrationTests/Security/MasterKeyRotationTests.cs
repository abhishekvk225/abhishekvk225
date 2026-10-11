using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Persistence;
using NexaVerify.Infrastructure.Background;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.Infrastructure.Platform.KeyRotation;
using NexaVerify.Infrastructure.Security;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.IntegrationTests.Security;

/// <summary>Master-key rotation against real SQL Server: data created under key A, rotated to B while the system keeps serving, A retired.</summary>
[Collection(SqlServerCollection.Name)]
public class MasterKeyRotationTests : IAsyncLifetime
{
    private const int ClientCount = 4;

    private readonly SqlServerFixture _fixture;
    private TestDb _db = null!;
    private readonly List<KeyRotationHarness> _harnesses = [];

    private readonly List<Guid> _clients = [];
    private readonly Dictionary<Guid, byte[]> _cipher = [];
    private readonly Dictionary<Guid, byte[]> _plain = [];
    private readonly List<Guid> _mfaUsers = [];
    private readonly List<Guid> _licenses = [];

    public MasterKeyRotationTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync(_fixture);
        await _db.EnsureClientAsync(NexaVerify.Domain.Common.PlatformTenant.ClientId); // platform-level audit rows hang off the platform tenant (seeded in real deployments)
    }

    public async Task DisposeAsync()
    {
        foreach (var h in _harnesses)
        {
            await h.DisposeAsync();
        }

        await SqlServerFixture.TryDropDatabaseAsync(_db.ConnectionString);
    }

    private KeyRotationHarness Harness(Dictionary<string, string?> encryption)
    {
        var h = KeyRotationHarness.Create(_db, encryption);
        _harnesses.Add(h);
        return h;
    }

    private KeyRotationHarness OnlyA() => Harness(KeyRotationHarness.Ring("a", ("a", 1)));

    private KeyRotationHarness AandBCurrentB() => Harness(KeyRotationHarness.Ring("b", ("a", 1), ("b", 2)));

    private KeyRotationHarness OnlyB() => Harness(KeyRotationHarness.Ring("b", ("b", 2)));

    /// <summary>Clients with data keys and encrypted data, MFA enrolments and anchored ledgers, all written under the harness's key.</summary>
    private async Task SeedAsync(KeyRotationHarness h, int clients = ClientCount, int licensesPerClient = 1)
    {
        for (var i = 0; i < clients; i++)
        {
            var client = Guid.NewGuid();
            _clients.Add(client);
            await h.AddClientAsync(client);
            _plain[client] = RandomNumberGenerator.GetBytes(48);
            _cipher[client] = await h.EncryptForClientAsync(client, _plain[client]);
            _mfaUsers.Add(await h.AddMfaUserAsync(client, $"user{i}@{client:N}.invalid"));
            for (var l = 0; l < licensesPerClient; l++)
            {
                _licenses.Add(await h.AddAnchoredLicenseAsync(client));
            }
        }
    }

    private async Task AssertEverythingReadableAsync(KeyRotationHarness h)
    {
        foreach (var client in _clients)
        {
            (await h.DecryptForClientColdAsync(client, _cipher[client])).ShouldBe(_plain[client]);
        }

        foreach (var user in _mfaUsers)
        {
            (await h.ReadMfaAsync(user)).ShouldBe(KeyRotationHarness.MfaPlaintext(user));
        }

        var verification = await h.VerifyAndAnchorAsync();
        verification.Broken.ShouldBe(0);
    }

    // ------------------------------------------------------------------ the full lifecycle

    [Fact]
    public async Task Rotate_from_A_to_B_then_retire_A_and_everything_works_with_only_B()
    {
        var a = OnlyA();
        await SeedAsync(a);
        (await a.UsageAsync()).Under(KeyArtifacts.ClientDataKey, "a").ShouldBe(ClientCount);

        var b = AandBCurrentB();
        var before = await b.UsageAsync();
        before.PercentComplete.ShouldBeLessThan(100);
        before.IsSafeToRetire("a").ShouldBeFalse();

        var report = await b.RotateAsync();

        report.Outcome.ShouldBe(RotationOutcome.Complete, report.Message);
        report.ClientKeysRewrapped.ShouldBe(ClientCount);
        report.MfaSecretsReencrypted.ShouldBe(ClientCount);
        report.LicensesReanchored.ShouldBe(_licenses.Count);
        report.CheckpointsResigned.ShouldBeGreaterThanOrEqualTo(_licenses.Count * 2);
        report.BrokenLedgers.ShouldBeEmpty();

        var after = await b.UsageAsync();
        after.PercentComplete.ShouldBe(100);
        after.IsSafeToRetire("a").ShouldBeTrue();
        after.Under(KeyArtifacts.ClientDataKey, "b").ShouldBe(ClientCount);
        after.Under(KeyArtifacts.MfaSecret, "b").ShouldBe(ClientCount);
        after.Under(KeyArtifacts.LedgerCheckpoint, "a").ShouldBe(0);

        // old checkpoints are retained (append-only) but superseded, in the same transaction as their replacements
        var superseded = await b.CountAsync(db => db.LedgerCheckpointSupersessions);
        superseded.ShouldBe(report.CheckpointsResigned);
        (await b.CountAsync(db => db.LedgerCheckpoints.Where(c => c.KeyId == "a"))).ShouldBe(superseded);

        // retire A: the application runs with B alone
        var onlyB = OnlyB();
        await AssertEverythingReadableAsync(onlyB);
        var verify = await onlyB.Services.GetRequiredService<KeyringVerifier>().VerifyAsync(10, default);
        verify.ExitCode.ShouldBe(0, string.Join("; ", verify.Failures));
    }

    [Fact]
    public async Task Running_the_rotation_again_changes_nothing()
    {
        var a = OnlyA();
        await SeedAsync(a, clients: 2);
        var b = AandBCurrentB();
        (await b.RotateAsync()).Outcome.ShouldBe(RotationOutcome.Complete);
        var checkpoints = await b.CountAsync(db => db.LedgerCheckpoints);

        var again = await b.RotateAsync();

        again.Outcome.ShouldBe(RotationOutcome.Complete);
        again.ClientKeysRewrapped.ShouldBe(0);
        again.MfaSecretsReencrypted.ShouldBe(0);
        again.LicensesReanchored.ShouldBe(0);
        again.CheckpointsResigned.ShouldBe(0);
        (await b.CountAsync(db => db.LedgerCheckpoints)).ShouldBe(checkpoints);
    }

    [Fact]
    public async Task A_dry_run_only_counts()
    {
        var a = OnlyA();
        await SeedAsync(a, clients: 2);
        var b = AandBCurrentB();

        var report = await b.RotateAsync(dryRun: true);

        report.DryRun.ShouldBeTrue();
        report.Outcome.ShouldBe(RotationOutcome.Incomplete);
        report.Before!.Pending(KeyArtifacts.ClientDataKey).ShouldBe(2);
        report.Before.Pending(KeyArtifacts.MfaSecret).ShouldBe(2);
        report.ClientKeysRewrapped.ShouldBe(0);
        (await b.UsageAsync()).Under(KeyArtifacts.ClientDataKey, "a").ShouldBe(2);
        (await b.CountAsync(db => db.LedgerCheckpointSupersessions)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ online: concurrent traffic

    [Fact]
    public async Task Rotation_while_requests_keep_reading_and_writing_never_fails_a_decrypt_and_rewraps_each_row_exactly_once()
    {
        var a = OnlyA();
        await SeedAsync(a, clients: 12, licensesPerClient: 0);
        var b = AandBCurrentB();
        var failures = new ConcurrentBag<string>();
        var reads = 0L;
        var born = new ConcurrentBag<Guid>();
        using var stop = new CancellationTokenSource();

        async Task Loop(Func<int, Task> body)
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await body(i++);
                    Interlocked.Increment(ref reads);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        var traffic = new[]
        {
            Loop(async i =>
            {
                var client = _clients[i % _clients.Count];
                (await b.DecryptForClientColdAsync(client, _cipher[client])).ShouldBe(_plain[client]);
            }),
            Loop(async i =>
            {
                var user = _mfaUsers[i % _mfaUsers.Count];
                (await b.ReadMfaAsync(user)).ShouldBe(KeyRotationHarness.MfaPlaintext(user));
            }),
            Loop(async i =>
            {
                // writers: new tenants and new enrolments appear (under B) while the old ones are being moved; MFA step bookkeeping writes the same rows
                var client = Guid.NewGuid();
                await b.AddClientAsync(client);
                born.Add(client);
                var user = await b.AddMfaUserAsync(client, $"live{i}@{client:N}.invalid");
                (await b.ReadMfaAsync(user)).ShouldBe(KeyRotationHarness.MfaPlaintext(user));
                await b.WithPlatformAsync(async (sp, _) => await sp.GetRequiredService<IMfaAtomics>().TryAdvanceStepAsync(_mfaUsers[i % _mfaUsers.Count], 1000 + i, default));
            }),
        };

        RotationReport report;
        try
        {
            await Task.Delay(300);
            report = await b.RotateAsync(batchSize: 2);
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(traffic);
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures.Distinct().Take(5)));
        reads.ShouldBeGreaterThan(20);
        report.Outcome.ShouldBe(RotationOutcome.Complete, report.Message);
        report.ClientKeysRewrapped.ShouldBe(12); // the 12 seeded keys, each exactly once; rows born under B were never touched
        report.MfaSecretsReencrypted.ShouldBe(12);
        var after = await b.UsageAsync();
        after.PercentComplete.ShouldBe(100);
        after.Under(KeyArtifacts.ClientDataKey, "b").ShouldBe(12 + born.Count);
        after.IsSafeToRetire("a").ShouldBeTrue();
        await AssertEverythingReadableAsync(OnlyB());
    }

    // ------------------------------------------------------------------ crash / resume

    [Fact]
    public async Task A_rotation_stopped_by_its_batch_budget_or_a_crash_resumes_and_finishes_without_redoing_work()
    {
        var a = OnlyA();
        await SeedAsync(a, clients: 6, licensesPerClient: 1);
        var b = AandBCurrentB();

        // 1. budget: one batch of two rows only
        var first = await b.RotateAsync(batchSize: 2, maxBatches: 1);
        first.Outcome.ShouldBe(RotationOutcome.Incomplete);
        var partial = await b.UsageAsync();
        partial.PercentComplete.ShouldBeGreaterThan(0);
        partial.PercentComplete.ShouldBeLessThan(100);
        partial.IsSafeToRetire("a").ShouldBeFalse();
        await AssertEverythingReadableAsync(b); // a half-rotated system reads everything

        // 2. crash: cancel mid-run (after the first rows moved)
        using var crash = new CancellationTokenSource();
        var running = b.RotateAsync(batchSize: 1, ct: crash.Token);
        await Task.Delay(150);
        await crash.CancelAsync();
        try
        {
            await running;
        }
        catch (OperationCanceledException)
        {
            // the process "died" here
        }

        await AssertEverythingReadableAsync(b);

        // 3. resume
        var resumed = await b.Services.GetRequiredService<MasterKeyRotationService>().RunAsync(new RotationRequest(false, 50, null, Resume: true), default);
        resumed.Outcome.ShouldBe(RotationOutcome.Complete, resumed.Message);

        var done = await b.UsageAsync();
        done.PercentComplete.ShouldBe(100);
        done.IsSafeToRetire("a").ShouldBeTrue();
        (await b.CountAsync(db => db.LedgerCheckpointSupersessions)).ShouldBe(await b.CountAsync(db => db.LedgerCheckpoints.Where(c => c.KeyId == "a")));
        await AssertEverythingReadableAsync(OnlyB());
    }

    // ------------------------------------------------------------------ never anchor over a broken chain

    [Fact]
    public async Task Rotation_refuses_a_tampered_ledger_anchors_nothing_over_it_and_touches_no_other_data()
    {
        var a = OnlyA();
        await SeedAsync(a, clients: 3);
        await a.TamperWithLedgerAsync(_licenses[1]);
        var b = AandBCurrentB();
        var checkpointsBefore = await b.CountAsync(db => db.LedgerCheckpoints);

        var report = await b.RotateAsync();

        report.Outcome.ShouldBe(RotationOutcome.LedgerBroken);
        report.BrokenLedgers.Select(x => x.LicenseId).ShouldContain(_licenses[1]);
        report.ClientKeysRewrapped.ShouldBe(0);
        report.MfaSecretsReencrypted.ShouldBe(0);
        // the tampered license has no new checkpoint and its old ones are not superseded
        (await b.CountAsync(db => db.LedgerCheckpoints.Where(c => c.LicenseId == _licenses[1] && c.KeyId == "b"))).ShouldBe(0);
        (await b.CountAsync(db => db.LedgerCheckpointSupersessions.Where(s => s.LicenseId == _licenses[1]))).ShouldBe(0);
        var usage = await b.UsageAsync();
        usage.Under(KeyArtifacts.ClientDataKey, "a").ShouldBe(3);
        usage.Under(KeyArtifacts.MfaSecret, "a").ShouldBe(3);
        (await b.CountAsync(db => db.LedgerCheckpoints)).ShouldBeGreaterThanOrEqualTo(checkpointsBefore);

        // verify-keyring reports the break with exit code 3
        (await b.Services.GetRequiredService<KeyringVerifier>().VerifyAsync(5, default)).ExitCode.ShouldBe(3);
    }

    [Fact]
    public async Task A_forged_checkpoint_blocks_rotation_too()
    {
        var a = OnlyA();
        await SeedAsync(a, clients: 1);
        await a.WithPlatformAsync(async (_, db) =>
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE licensing.LedgerCheckpoints DISABLE TRIGGER trg_LedgerCheckpoints_AppendOnly");
            try
            {
                await db.Database.ExecuteSqlRawAsync("UPDATE licensing.LedgerCheckpoints SET BalanceAfter = BalanceAfter + 100");
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE licensing.LedgerCheckpoints ENABLE TRIGGER trg_LedgerCheckpoints_AppendOnly");
            }
        });

        var report = await AandBCurrentB().RotateAsync();

        report.Outcome.ShouldBe(RotationOutcome.LedgerBroken);
        report.BrokenLedgers.Single().Reason.ShouldContain("signature");
    }

    [Fact]
    public async Task Rotation_refuses_to_start_when_live_data_names_a_key_that_is_not_in_the_ring()
    {
        var a = OnlyA();
        await SeedAsync(a, clients: 2);
        var withoutA = OnlyB(); // A is gone from the ring but the data still needs it

        var report = await withoutA.RotateAsync();

        report.Outcome.ShouldBe(RotationOutcome.Failed);
        report.Message!.ShouldContain("not in the keyring");
        report.Message!.ShouldContain("a");
        (await withoutA.UsageAsync()).UnknownKeyIds.ShouldBe(["a"]);
        await Should.ThrowAsync<CryptographicException>(() => withoutA.DecryptForClientColdAsync(_clients[0], _cipher[_clients[0]])); // clear error, no silent fallback
        await Should.ThrowAsync<UnknownMasterKeyException>(() => withoutA.ReadMfaAsync(_mfaUsers[0]));
    }

    [Fact]
    public async Task A_key_that_was_rotated_away_from_cannot_anchor_the_same_ledgers_again_so_the_tool_asks_for_a_fresh_key()
    {
        var a = OnlyA();
        await SeedAsync(a, clients: 2);
        var b = AandBCurrentB();
        (await b.RotateAsync()).Outcome.ShouldBe(RotationOutcome.Complete);

        var backToA = Harness(KeyRotationHarness.Ring("a", ("a", 1), ("b", 2)));
        var refused = await backToA.RotateAsync();

        refused.Outcome.ShouldBe(RotationOutcome.Failed);
        refused.Message.ShouldNotBeNull().ShouldContain("never been used");
        (await backToA.UsageAsync()).Under(KeyArtifacts.ClientDataKey, "b").ShouldBe(2); // nothing was touched

        // a fresh key works, and everything is readable with only that key
        var c = Harness(KeyRotationHarness.Ring("c", ("a", 1), ("b", 2), ("c", 3)));
        (await c.RotateAsync()).Outcome.ShouldBe(RotationOutcome.Complete);
        (await c.UsageAsync()).IsSafeToRetire("b").ShouldBeTrue();
        await AssertEverythingReadableAsync(Harness(KeyRotationHarness.Ring("c", ("c", 3))));
    }

    [Fact]
    public async Task Rotation_refuses_an_ephemeral_keyring_and_a_bad_batch_size()
    {
        var ephemeral = Harness(new Dictionary<string, string?> { ["Encryption:AllowEphemeralKey"] = "true" });
        (await ephemeral.RotateAsync()).Outcome.ShouldBe(RotationOutcome.Failed);

        var b = AandBCurrentB();
        (await b.RotateAsync(batchSize: 0)).Outcome.ShouldBe(RotationOutcome.Failed);
    }

    // ------------------------------------------------------------------ status

    [Fact]
    public async Task Rotation_status_counts_per_key_and_artifact_and_knows_when_a_key_can_be_retired()
    {
        var a = OnlyA();
        await SeedAsync(a, clients: 3, licensesPerClient: 1);
        var b = AandBCurrentB();
        var totalCheckpoints = await b.CountAsync(db => db.LedgerCheckpoints);

        var start = await b.UsageAsync();
        start.Counts[KeyArtifacts.ClientDataKey]["a"].ShouldBe(3);
        start.Counts[KeyArtifacts.MfaSecret]["a"].ShouldBe(3);
        start.Counts[KeyArtifacts.LedgerCheckpoint]["a"].ShouldBe(totalCheckpoints);
        start.PercentComplete.ShouldBe(0);
        start.IsSafeToRetire("a").ShouldBeFalse();
        start.IsSafeToRetire("b").ShouldBeFalse(); // the current key is never retirable
        start.UnknownKeyIds.ShouldBeEmpty();

        await b.RotateAsync(batchSize: 1, maxBatches: 2); // client keys: 2 of 3 (ledgers first: 2 licenses...)
        var middle = await b.UsageAsync();
        middle.Total(KeyArtifacts.ClientDataKey).ShouldBe(3);
        middle.Total(KeyArtifacts.MfaSecret).ShouldBe(3);
        (middle.Under(KeyArtifacts.ClientDataKey, "a") + middle.Under(KeyArtifacts.ClientDataKey, "b")).ShouldBe(3);
        middle.PercentComplete.ShouldBeGreaterThan(0);
        middle.PercentComplete.ShouldBeLessThan(100);
        middle.IsSafeToRetire("a").ShouldBeFalse();

        await b.RotateAsync();
        var end = await b.UsageAsync();
        end.PercentComplete.ShouldBe(100);
        end.IsSafeToRetire("a").ShouldBeTrue();
        end.Under(KeyArtifacts.LedgerCheckpoint, "b").ShouldBe(totalCheckpoints);
    }

    // ------------------------------------------------------------------ legacy upgrade

    [Fact]
    public async Task A_deployment_with_only_the_old_single_key_setting_keeps_working_and_its_data_can_be_rotated()
    {
        // data as the pre-keyring code wrote it: MFA without key-id header, wrapped keys and checkpoints stamped "m1"
        var legacy = Harness(KeyRotationHarness.Legacy(1));
        for (var i = 0; i < 3; i++)
        {
            var client = Guid.NewGuid();
            _clients.Add(client);
            await legacy.AddClientAsync(client);
            _plain[client] = RandomNumberGenerator.GetBytes(40);
            _cipher[client] = await legacy.EncryptForClientAsync(client, _plain[client]);
            _mfaUsers.Add(await legacy.AddLegacyFormatMfaUserAsync(client, $"old{i}@{client:N}.invalid", masterFill: 1));
            _licenses.Add(await legacy.AddAnchoredLicenseAsync(client));
        }

        legacy.Master.CurrentKeyId.ShouldBe("m1");
        var legacyUsage = await legacy.UsageAsync();
        legacyUsage.Counts[KeyArtifacts.MfaSecret].ShouldBe(new Dictionary<string, long> { ["m1"] = 3 }); // header-less payloads belong to the legacy id
        await AssertEverythingReadableAsync(legacy);

        // upgrade: add key b, keep the old setting; nothing is lost and the old key stays current until CurrentKeyId changes
        var upgraded = Harness(new Dictionary<string, string?>(KeyRotationHarness.Legacy(1))
        {
            ["Encryption:Keys:0:Id"] = "b",
            ["Encryption:Keys:0:KeyBase64"] = Convert.ToBase64String(KeyRotationHarness.KeyBytes(2)),
        });
        upgraded.Master.CurrentKeyId.ShouldBe("m1");
        await AssertEverythingReadableAsync(upgraded);

        var rotating = Harness(new Dictionary<string, string?>(KeyRotationHarness.Legacy(1))
        {
            ["Encryption:Keys:0:Id"] = "b",
            ["Encryption:Keys:0:KeyBase64"] = Convert.ToBase64String(KeyRotationHarness.KeyBytes(2)),
            ["Encryption:CurrentKeyId"] = "b",
        });
        var report = await rotating.RotateAsync();
        report.Outcome.ShouldBe(RotationOutcome.Complete, report.Message);
        report.MfaSecretsReencrypted.ShouldBe(3);
        (await rotating.UsageAsync()).IsSafeToRetire("m1").ShouldBeTrue();
        await AssertEverythingReadableAsync(OnlyB());
    }

    // ------------------------------------------------------------------ lazy re-wrap + health

    [Fact]
    public async Task The_lazy_job_moves_client_keys_and_MFA_secrets_in_small_batches_and_leaves_ledgers_to_the_command()
    {
        var a = OnlyA();
        await SeedAsync(a, clients: 5);
        var settings = KeyRotationHarness.Ring("b", ("a", 1), ("b", 2));
        settings["Encryption:LazyRewrap"] = "true";
        settings["Encryption:LazyRewrapBatchSize"] = "2";
        var b = Harness(settings);
        var job = new KeyRewrapJob(
            b.Services.GetRequiredService<MasterKeyRotationService>(), b.Services.GetRequiredService<NexaVerify.Application.Licensing.IDistributedLock>(),
            b.Services.GetRequiredService<IOptions<EncryptionOptions>>(), NullLogger<KeyRewrapJob>.Instance);

        (await job.RunOnceAsync(default)).ShouldBe(2 + 5); // 2 client keys (one batch) + all 5 MFA secrets
        (await job.RunOnceAsync(default)).ShouldBe(2);
        (await job.RunOnceAsync(default)).ShouldBe(1);
        (await job.RunOnceAsync(default)).ShouldBe(0);

        var usage = await b.UsageAsync();
        usage.Pending(KeyArtifacts.ClientDataKey).ShouldBe(0);
        usage.Pending(KeyArtifacts.MfaSecret).ShouldBe(0);
        usage.Pending(KeyArtifacts.LedgerCheckpoint).ShouldBeGreaterThan(0); // re-anchoring is the explicit command's job
        await AssertEverythingReadableAsync(b);
    }

    [Fact]
    public async Task The_keyring_health_check_reports_ids_only_and_fails_on_unknown_ids_in_use()
    {
        var a = OnlyA();
        await SeedAsync(a, clients: 2);

        var degraded = await Check(AandBCurrentB());
        degraded.Status.ShouldBe(HealthStatus.Degraded);
        degraded.Data["currentKeyId"].ShouldBe("b");
        ((string[])degraded.Data["keyIds"]).ShouldBe(["a", "b"]);
        degraded.Data.Values.Select(v => v.ToString()).ShouldNotContain(Convert.ToBase64String(KeyRotationHarness.KeyBytes(1)));

        var unhealthy = await Check(OnlyB());
        unhealthy.Status.ShouldBe(HealthStatus.Unhealthy);
        ((string[])unhealthy.Data["unknownKeyIdsInUse"]).ShouldBe(["a"]);

        var b = AandBCurrentB();
        await b.RotateAsync();
        (await Check(OnlyB())).Status.ShouldBe(HealthStatus.Healthy);

        static async Task<HealthCheckResult> Check(KeyRotationHarness h) =>
            await h.Services.GetRequiredService<KeyringHealthCheck>().CheckHealthAsync(new HealthCheckContext());
    }

    [Fact]
    public async Task Verify_keyring_catches_a_key_that_cannot_decrypt()
    {
        var a = OnlyA();
        await SeedAsync(a, clients: 2);
        var impostor = Harness(KeyRotationHarness.Ring("a", ("a", 9))); // same id, different material

        var result = await impostor.Services.GetRequiredService<KeyringVerifier>().VerifyAsync(5, default);

        result.ExitCode.ShouldBe(1);
        result.Failures.ShouldNotBeEmpty();
        result.Failures.ShouldAllBe(f => !f.Contains(Convert.ToBase64String(KeyRotationHarness.KeyBytes(9))));
    }
}
