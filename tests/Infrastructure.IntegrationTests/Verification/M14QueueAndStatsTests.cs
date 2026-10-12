using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Modules;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Verification;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.IntegrationTests.Verification;

[Collection(SqlServerCollection.Name)]
public class M14QueueAndStatsTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private M14Rig _rig = null!;

    public M14QueueAndStatsTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => _rig = await M14Rig.CreateAsync(_fixture);

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask();

    private async Task<List<Guid>> SeedQueuedAsync(Guid client, int count, DateTime now, DateTime? dueAt = null)
    {
        var ids = new List<Guid>();
        await _rig.TenantAsync(client, async sp =>
        {
            for (var i = 0; i < count; i++)
            {
                var r = M14Rig.NewRequest(client, dueAt ?? now);
                sp.GetRequiredService<IVerificationRepository>().Add(r);
                ids.Add(r.Id);
            }

            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        });
        return ids;
    }

    private Task<IReadOnlyList<ClaimedVerification>> ClaimAsync(int batch, int perClient, DateTime now, Guid token, TimeSpan? lease = null) =>
        _rig.PlatformAsync(sp => sp.GetRequiredService<IVerificationQueue>().ClaimDueAsync(batch, perClient, now, lease ?? TimeSpan.FromMinutes(5), token, default));

    [Fact]
    public async Task Two_concurrent_claimers_never_receive_the_same_request()
    {
        var now = DateTime.UtcNow;
        var clients = new[] { await _rig.NewClientAsync(), await _rig.NewClientAsync(), await _rig.NewClientAsync() };
        var all = new List<Guid>();
        foreach (var c in clients)
        {
            all.AddRange(await SeedQueuedAsync(c, 20, now));
        }

        var tokenA = Guid.NewGuid();
        var tokenB = Guid.NewGuid();
        var claims = await Task.WhenAll(
            Task.Run(() => ClaimAsync(100, 100, now.AddSeconds(1), tokenA)),
            Task.Run(() => ClaimAsync(100, 100, now.AddSeconds(1), tokenB)),
            Task.Run(() => ClaimAsync(100, 100, now.AddSeconds(1), Guid.NewGuid())));

        var ids = claims.SelectMany(c => c).Select(c => c.RequestId).ToList();
        ids.Count.ShouldBe(60);
        ids.Distinct().Count().ShouldBe(60);
        ids.ShouldBe(all, ignoreOrder: true);
        claims.SelectMany(c => c).ShouldAllBe(c => c.Attempts == 1 && c.Type == VerificationType.Pan);

        // everything is leased now: nothing left, and each claim wrote its Processing event
        (await ClaimAsync(100, 100, now.AddSeconds(2), Guid.NewGuid())).ShouldBeEmpty();
        await _rig.TenantAsync(clients[0], async sp =>
        {
            var repo = sp.GetRequiredService<IVerificationRepository>();
            var request = await repo.GetReadOnlyAsync(all[0], default);
            request!.Status.ShouldBe(VerificationStatus.Processing);
            request.LeaseToken.ShouldNotBeNull();
            request.StartedAt.ShouldNotBeNull();
            var events = await repo.ListEventsAsync(all[0], 10, default);
            events.Select(e => e.ToStatus).ShouldBe([VerificationStatus.Queued, VerificationStatus.Processing]);
        });
    }

    [Fact]
    public async Task Claim_respects_due_time_per_client_cap_and_batch_size()
    {
        var now = DateTime.UtcNow;
        var busy = await _rig.NewClientAsync();
        var quiet = await _rig.NewClientAsync();
        await SeedQueuedAsync(busy, 10, now);
        var quietIds = await SeedQueuedAsync(quiet, 2, now);
        var future = await SeedQueuedAsync(quiet, 1, now, dueAt: now.AddHours(1));

        var first = await ClaimAsync(100, 3, now.AddSeconds(1), Guid.NewGuid());
        first.Count(c => c.ClientId == busy).ShouldBe(3);
        first.Count(c => c.ClientId == quiet).ShouldBe(2);
        first.ShouldNotContain(c => future.Contains(c.RequestId));
        first.Select(c => c.RequestId).ShouldBeSubsetOf(quietIds.Concat(await IdsOfAsync(busy)));

        var limited = await ClaimAsync(4, 100, now.AddSeconds(1), Guid.NewGuid());
        limited.Count.ShouldBe(4);
    }

    private Task<List<Guid>> IdsOfAsync(Guid client) =>
        _rig.TenantAsync(client, sp => sp.GetRequiredService<AppDbContext>().VerificationRequests.AsNoTracking().Select(r => r.Id).ToListAsync());

    [Fact]
    public async Task Lease_lifecycle_extend_requeue_fail_release_and_expire()
    {
        var now = DateTime.UtcNow;
        var client = await _rig.NewClientAsync();
        var ids = await SeedQueuedAsync(client, 1, now);
        var token = Guid.NewGuid();
        var claimed = (await ClaimAsync(10, 10, now.AddSeconds(1), token, TimeSpan.FromMinutes(1))).Single();

        var queue = (IServiceProvider sp) => sp.GetRequiredService<IVerificationQueue>();
        (await _rig.PlatformAsync(sp => queue(sp).ExtendLeaseAsync(claimed.RequestId, Guid.NewGuid(), now.AddHours(1), now, default))).ShouldBeFalse();
        (await _rig.PlatformAsync(sp => queue(sp).ExtendLeaseAsync(claimed.RequestId, token, now.AddMinutes(10), now, default))).ShouldBeTrue();

        // lease not expired yet
        (await _rig.PlatformAsync(sp => queue(sp).RequeueExpiredLeasesAsync(now.AddMinutes(5), 3, 10, default))).ShouldBe(new LeaseSweepResult(0, 0));
        // expired, attempts (1) < max (3): back to Queued, due now
        (await _rig.PlatformAsync(sp => queue(sp).RequeueExpiredLeasesAsync(now.AddMinutes(11), 3, 10, default))).ShouldBe(new LeaseSweepResult(1, 0));

        // claimed again: attempt 2; then graceful release gives the attempt back
        var again = (await ClaimAsync(10, 10, now.AddMinutes(12), token)).Single();
        again.Attempts.ShouldBe(2);
        (await _rig.PlatformAsync(sp => queue(sp).ReleaseHeldAsync(token, now.AddMinutes(13), now.AddMinutes(12), default))).ShouldBe(1);
        (await ClaimAsync(10, 10, now.AddMinutes(12), token)).ShouldBeEmpty(); // not due before notBefore
        var third = (await ClaimAsync(10, 10, now.AddMinutes(14), token, TimeSpan.FromMinutes(1))).Single();
        third.Attempts.ShouldBe(2);

        // out of attempts: lease expiry fails it with PROCESSING_TIMEOUT
        (await _rig.PlatformAsync(sp => queue(sp).RequeueExpiredLeasesAsync(now.AddMinutes(16), 2, 10, default))).ShouldBe(new LeaseSweepResult(0, 1));
        await _rig.TenantAsync(client, async sp =>
        {
            var r = await sp.GetRequiredService<IVerificationRepository>().GetReadOnlyAsync(ids[0], default);
            r!.Status.ShouldBe(VerificationStatus.Failed);
            r.ErrorCode.ShouldBe("PROCESSING_TIMEOUT");
            r.LeaseToken.ShouldBeNull();
            (await sp.GetRequiredService<IVerificationRepository>().ListEventsAsync(ids[0], 20, default)).Last().ToStatus.ShouldBe(VerificationStatus.Failed);
        });

        // stale queued requests expire without being charged
        var old = await SeedQueuedAsync(client, 2, now);
        (await _rig.PlatformAsync(sp => queue(sp).ExpireStaleQueuedAsync(DateTime.UtcNow.AddMinutes(1), DateTime.UtcNow, 10, default))).ShouldBe(2);
        await _rig.TenantAsync(client, async sp =>
        {
            var r = await sp.GetRequiredService<IVerificationRepository>().GetReadOnlyAsync(old[0], default);
            r!.Status.ShouldBe(VerificationStatus.Expired);
            r.CreditsCharged.ShouldBe(0);
        });
    }

    [Fact]
    public async Task Tenant_scope_claim_only_sees_its_own_rows()
    {
        var now = DateTime.UtcNow;
        var a = await _rig.NewClientAsync();
        var b = await _rig.NewClientAsync();
        await SeedQueuedAsync(a, 2, now);
        await SeedQueuedAsync(b, 3, now);
        var claimed = await _rig.TenantAsync(b, sp => sp.GetRequiredService<IVerificationQueue>().ClaimDueAsync(50, 50, now.AddSeconds(1), TimeSpan.FromMinutes(1), Guid.NewGuid(), default));
        claimed.Count.ShouldBe(3);
        claimed.ShouldAllBe(c => c.ClientId == b);
    }

    [Fact]
    public async Task Admin_stats_are_correct_and_metadata_only()
    {
        var client = await _rig.NewClientAsync();
        var other = await _rig.NewClientAsync();
        var t0 = DateTime.UtcNow;
        await _rig.TenantAsync(client, async sp =>
        {
            var repo = sp.GetRequiredService<IVerificationRepository>();
            VerificationRequest Started(VerificationType type, int seconds)
            {
                var r = M14Rig.NewRequest(client, t0, type);
                r.Claim(Guid.NewGuid(), t0.AddMinutes(5), t0);
                repo.Add(r);
                return r;
            }

            var p1 = Started(VerificationType.Pan, 1);
            p1.RecordExtractionCharge(1, null);
            p1.Complete(t0.AddSeconds(1));

            var p2 = Started(VerificationType.Pan, 2);
            p2.RecordDocumentStage(StageState.Completed, StageState.Completed, StageState.Completed, []);
            p2.MarkExternalPending();
            p2.RecordExternalResult(ExternalState.Verified, "prov-1", "ref-1", t0.AddSeconds(2));
            p2.RecordExtractionCharge(1, null);
            p2.RecordProviderCharge(2, null);
            p2.Complete(t0.AddSeconds(2));

            var bank = Started(VerificationType.BankStatement, 3);
            bank.AddReviewReason("BALANCE_MISMATCH");
            bank.RecordExtractionCharge(5, null);
            bank.Complete(t0.AddSeconds(3));

            var failed = Started(VerificationType.Passport, 4);
            failed.Fail("DOCUMENT_UNREADABLE", t0.AddSeconds(4));

            var timeout = Started(VerificationType.Passport, 5);
            timeout.MarkExternalPending();
            timeout.RecordExternalResult(ExternalState.Timeout, "prov-2", null, t0.AddSeconds(5));
            timeout.Complete(t0.AddSeconds(5));

            repo.Add(M14Rig.NewRequest(client, t0, VerificationType.Pan)); // stays Queued
            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        });
        await SeedQueuedAsync(other, 4, t0); // another tenant must not leak into the filtered numbers

        var query = new VerificationStatsQuery { ClientId = client, From = t0.AddMinutes(-5), To = DateTime.UtcNow.AddMinutes(5) };
        var stats = await _rig.PlatformAsync(sp => sp.GetRequiredService<IVerificationAdminQueries>().GetStatsAsync(query, t0.AddSeconds(30), default));

        stats.Total.ShouldBe(6);
        stats.ByType.ToDictionary(x => x.Key, x => x.Count).OrderBy(x => x.Key).ShouldBe(new Dictionary<string, int> { ["Pan"] = 3, ["BankStatement"] = 1, ["Passport"] = 2 }.OrderBy(x => x.Key));
        stats.ByStatus.ToDictionary(x => x.Key, x => x.Count).OrderBy(x => x.Key).ShouldBe(new Dictionary<string, int> { ["Completed"] = 2, ["ReviewRequired"] = 2, ["Failed"] = 1, ["Queued"] = 1 }.OrderBy(x => x.Key));
        stats.ReviewRequired.ShouldBe(2); // the bank statement and the provider timeout
        stats.FailuresByErrorCode.ShouldHaveSingleItem().ShouldBe(new VerificationCountDto("DOCUMENT_UNREADABLE", 1));
        stats.ProviderOutcomes.ToDictionary(x => x.Key, x => x.Count).OrderBy(x => x.Key).ShouldBe(new Dictionary<string, int> { ["Verified"] = 1, ["Timeout"] = 1 }.OrderBy(x => x.Key));
        stats.ExtractionCreditsCharged.ShouldBe(7);
        stats.ProviderCreditsCharged.ShouldBe(2);
        stats.QueueDepth.ShouldBe(1);
        stats.OldestQueuedAgeSeconds.ShouldNotBeNull();
        stats.P50ProcessingSeconds.ShouldBe(3.0);
        stats.P95ProcessingSeconds.ShouldBe(5.0);

        var series = await _rig.PlatformAsync(sp => sp.GetRequiredService<IVerificationAdminQueries>().GetTimeSeriesAsync(query, default));
        series.Sum(p => p.Total).ShouldBe(6);
        series.Where(p => p.Type == VerificationType.Pan).Sum(p => p.CreditsCharged).ShouldBe(4);
        series.Sum(p => p.Failed).ShouldBe(1);

        var providers = await _rig.PlatformAsync(sp => sp.GetRequiredService<IVerificationAdminQueries>().GetProviderOutcomesAsync(t0.AddMinutes(-1), default));
        providers.Single(p => p.ProviderId == "prov-1").Counts.Verified.ShouldBe(1);
        providers.Single(p => p.ProviderId == "prov-2").Counts.Timeout.ShouldBe(1);

        var tenantView = await _rig.TenantAsync(other, sp => sp.GetRequiredService<IVerificationAdminQueries>().GetStatsAsync(new VerificationStatsQuery(), t0, default));
        tenantView.Total.ShouldBe(4); // tenant scope: only its own rows

        var (items, total) = await _rig.PlatformAsync(sp => sp.GetRequiredService<IVerificationAdminQueries>().ListForClientAsync(client, new AdminVerificationListQuery { PageSize = 3, Type = "Pan" }, default));
        total.ShouldBe(3);
        items.Count.ShouldBe(3);
        var detail = await _rig.PlatformAsync(sp => sp.GetRequiredService<IVerificationAdminQueries>().GetDetailAsync(client, items[0].RequestId, default));
        detail!.Events.ShouldNotBeEmpty();
        (await _rig.PlatformAsync(sp => sp.GetRequiredService<IVerificationAdminQueries>().GetDetailAsync(other, items[0].RequestId, default))).ShouldBeNull();
    }

    [Fact]
    public async Task List_filters_and_pages()
    {
        var client = await _rig.NewClientAsync();
        var now = DateTime.UtcNow;
        var ids = await SeedQueuedAsync(client, 5, now);
        var page = await _rig.TenantAsync(client, sp => sp.GetRequiredService<IVerificationRepository>().ListAsync(new VerificationListFilter(Status: VerificationStatus.Queued), 1, 2, default));
        page.Total.ShouldBe(5);
        page.Items.Count.ShouldBe(2);
        (await _rig.TenantAsync(client, sp => sp.GetRequiredService<IVerificationRepository>().ListAsync(new VerificationListFilter(Status: VerificationStatus.Failed), 0, 10, default))).Total.ShouldBe(0);
        (await _rig.TenantAsync(client, sp => sp.GetRequiredService<IVerificationRepository>().ListAsync(new VerificationListFilter(Search: ids[0].ToString()[..30]), 0, 10, default))).Total.ShouldBe(1);
        (await _rig.TenantAsync(client, sp => sp.GetRequiredService<IVerificationRepository>().ListAsync(new VerificationListFilter(Search: ids[0].ToString()), 0, 10, default))).Total.ShouldBe(1);
        (await _rig.TenantAsync(client, sp => sp.GetRequiredService<IVerificationRepository>().ListAsync(new VerificationListFilter(Search: "ref-"), 0, 10, default))).Total.ShouldBe(5);
        (await _rig.TenantAsync(client, sp => sp.GetRequiredService<IVerificationRepository>().CountOpenAsync(default))).ShouldBe(5);
    }

    [Fact]
    public async Task Cost_rules_modules_and_permissions_are_seeded()
    {
        await _rig.PlatformAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            foreach (var (operation, credits) in VerificationOperations.DefaultCredits)
            {
                var rule = await db.CostRules.AsNoTracking().SingleAsync(r => r.PlanId == null && r.Operation == operation);
                rule.Credits.ShouldBe(credits);
                rule.ChargePolicy.ShouldBe(ChargePolicy.OnCompleted);
            }

            (await db.CostRules.CountAsync(r => r.PlanId == null)).ShouldBe(4 + 5);
            (await db.VerificationModuleSettings.AsNoTracking().OrderBy(s => s.ModuleKey).ToListAsync()).Select(s => (s.ModuleKey, s.IsEnabled))
                .ShouldBe([("bank-statement", true), ("pan", true), ("passport", true)]);

            var keys = new[]
            {
                Permissions.Verification.BankStatementSubmit, Permissions.Verification.PanSubmit, Permissions.Verification.PassportSubmit, Permissions.Verification.Read,
                Permissions.Verification.ReportDownload, Permissions.Verification.DocumentDownload, Permissions.Verification.Erase,
                Permissions.VerificationAdmin.Read, Permissions.VerificationAdmin.Configure,
            };
            (await db.Permissions.AsNoTracking().Where(p => keys.Contains(p.Key)).CountAsync()).ShouldBe(9);
            var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).ToListAsync();
            var byId = await db.Permissions.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Key);
            HashSet<string> Granted(string role) => roles.Single(r => r.Name == role).Permissions.Select(p => byId[p.PermissionId]).ToHashSet();
            Granted(SystemRoles.ClientAdmin).ShouldBeSubsetOf(Permissions.All.Select(p => p.Key));
            Granted(SystemRoles.ClientAdmin).ShouldContain(Permissions.Verification.Read);
            Granted(SystemRoles.ClientAdmin).ShouldNotContain(Permissions.VerificationAdmin.Read);
            Granted(SystemRoles.SuperAdmin).ShouldContain(Permissions.VerificationAdmin.Configure);
            Granted(SystemRoles.SuperAdmin).ShouldNotContain(Permissions.Verification.PanSubmit);
            keys.Where(k => Granted(SystemRoles.ClientUser).Contains(k)).ShouldBeEmpty();
        });

        // seeding twice changes nothing and never overwrites an operator decision
        await _rig.PlatformAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.VerificationModuleSettings.SingleAsync(s => s.ModuleKey == "pan")).SetEnabled(false, DateTime.UtcNow, null);
            (await db.CostRules.SingleAsync(r => r.PlanId == null && r.Operation == MeteredOperation.PanExtraction)).CloseAt(DateTime.UtcNow);
            db.CostRules.Add(CostRule.Create(null, MeteredOperation.PanExtraction, 9, ChargePolicy.OnCompleted, DateTime.UtcNow));
            await db.SaveChangesAsync();
            await sp.GetRequiredService<NexaVerify.Infrastructure.Persistence.Seed.LicensingSeeder>().SeedAsync(default);
            (await db.VerificationModuleSettings.AsNoTracking().SingleAsync(s => s.ModuleKey == "pan")).IsEnabled.ShouldBeFalse();
            (await db.CostRules.AsNoTracking().CountAsync(r => r.PlanId == null && r.Operation == MeteredOperation.PanExtraction)).ShouldBe(2); // closed + new: not re-seeded
        });
    }

    [Fact]
    public async Task Operation_check_constraints_accept_the_five_new_operations()
    {
        var client = await _rig.NewClientAsync();
        await _rig.TenantAsync(client, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            foreach (var operation in VerificationOperations.DefaultCredits.Keys)
            {
                db.ClientCostRules.Add(ClientCostRule.Create(client, operation, 3, ChargePolicy.OnCompleted, DateTime.UtcNow));
            }

            await db.SaveChangesAsync();
            (await db.ClientCostRules.CountAsync()).ShouldBe(5);
        });

        foreach (var (table, constraint) in new[] { ("CostRules", "CK_CostRules_Operation"), ("LicenseTransactions", "CK_LicenseTransactions_Operation"), ("ClientCostRules", "CK_ClientCostRules_Operation") })
        {
            (await _rig.ScalarAsync<string>($"SELECT definition FROM sys.check_constraints WHERE name = '{constraint}'")).ShouldContain("PassportProviderCheck", customMessage: table);
        }
    }

    [Fact]
    public async Task Module_switch_semantics_missing_row_enabled_unknown_disabled_upcoming_unchanged()
    {
        await _rig.PlatformAsync(async sp =>
        {
            var reader = sp.GetRequiredService<IVerificationModuleSettingReader>();
            var registry = sp.GetRequiredService<IVerificationModuleRegistry>();
            (await reader.IsEnabledAsync("bank-statement", default)).ShouldBe(true);
            (await reader.IsEnabledAsync("no-such-module", default)).ShouldBeNull();
            (await registry.GetEffectiveStatusAsync("passport", default)).ShouldBe(ModuleStatus.Available);
            (await registry.GetEffectiveStatusAsync("no-such-module", default)).ShouldBe(ModuleStatus.Disabled);
            (await registry.GetEffectiveStatusAsync("aadhaar", default)).ShouldBe(ModuleStatus.Upcoming);

            var settings = sp.GetRequiredService<IVerificationModuleSettings>();
            (await settings.GetAsync("passport", default))!.SetEnabled(false, DateTime.UtcNow, null);
            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        });

        await _rig.PlatformAsync(async sp =>
        {
            (await sp.GetRequiredService<IVerificationModuleSettingReader>().IsEnabledAsync("passport", default)).ShouldBe(false);
            (await sp.GetRequiredService<IVerificationModuleRegistry>().GetEffectiveStatusAsync("passport", default)).ShouldBe(ModuleStatus.Disabled);
            (await sp.GetRequiredService<IVerificationModuleRegistry>().GetEffectiveStatusAsync("aadhaar", default)).ShouldBe(ModuleStatus.Upcoming);
        });

        // a module without a row is enabled (the switch only turns modules off)
        await _rig.PlatformAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.VerificationModuleSettings.Remove(await db.VerificationModuleSettings.SingleAsync(s => s.ModuleKey == "passport"));
            await db.SaveChangesAsync();
            (await sp.GetRequiredService<IVerificationModuleRegistry>().GetEffectiveStatusAsync("passport", default)).ShouldBe(ModuleStatus.Available);
        });
    }

    [Fact]
    public async Task Module_setting_updates_use_optimistic_concurrency()
    {
        await _rig.PlatformAsync(async sp1 =>
        {
            var one = await sp1.GetRequiredService<IVerificationModuleSettings>().GetAsync("bank-statement", default);
            await _rig.PlatformAsync(async sp2 =>
            {
                (await sp2.GetRequiredService<IVerificationModuleSettings>().GetAsync("bank-statement", default))!.SetEnabled(false, DateTime.UtcNow, null);
                await sp2.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            });
            one!.SetEnabled(true, DateTime.UtcNow, null);
            await Should.ThrowAsync<NexaVerify.Application.Common.ConcurrencyConflictException>(() => sp1.GetRequiredService<IUnitOfWork>().SaveChangesAsync());
        });
    }

    [Fact]
    public async Task Model_has_no_pending_changes_and_checks_replace_by_stage()
    {
        await _rig.PlatformAsync(sp => Task.FromResult(sp.GetRequiredService<AppDbContext>().Database.HasPendingModelChanges())).ContinueWith(t => t.Result.ShouldBeFalse());

        var client = await _rig.NewClientAsync();
        var id = (await SeedQueuedAsync(client, 1, DateTime.UtcNow))[0];
        await _rig.TenantAsync(client, async sp =>
        {
            var store = sp.GetRequiredService<IVerificationResultStore>();
            VerificationCheck C(int i, string code, CheckStage stage) => VerificationCheck.Create(client, id, i, code, stage, CheckOutcome.Pass, CheckSeverity.Info, null, "ok");
            await store.ReplaceChecksAsync(id, [CheckStage.Rules, CheckStage.External], [C(0, "A", CheckStage.Rules), C(1, "B", CheckStage.External)], default);
            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            await store.ReplaceChecksAsync(id, [CheckStage.External], [C(2, "B2", CheckStage.External)], default);
            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            (await store.ListChecksAsync(id, default)).Select(c => c.Code).ShouldBe(["A", "B2"]);
        });
    }
}
