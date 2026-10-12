using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Common;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Domain.Verification;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.IntegrationTests.Verification;

[Collection(SqlServerCollection.Name)]
public class M14IsolationTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private M14Rig _rig = null!;
    private Guid _a;
    private Guid _b;
    private Guid _requestA;
    private Guid _documentA;

    public M14IsolationTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _rig = await M14Rig.CreateAsync(_fixture);
        _a = await _rig.NewClientAsync();
        _b = await _rig.NewClientAsync();
        var now = DateTime.UtcNow;
        await _rig.TenantAsync(_a, async sp =>
        {
            var request = M14Rig.NewRequest(_a, now);
            var document = VerificationDocument.Create(_a, request.Id, DocumentKind.Pdf, "application/pdf", "pdf", 10, 1, new byte[32], now.AddDays(7));
            var blob = VerificationDocumentBlob.Create(_a, document.Id, [1, 2, 3, 4]);
            var outcome = await sp.GetRequiredService<IVerificationRepository>().CreateIdempotentAsync(request, document, blob, default);
            outcome.Created.ShouldBeTrue();
            await sp.GetRequiredService<IVerificationResultStore>().ReplaceResultAsync(
                VerificationResult.Create(_a, request.Id, 1, [9, 9], null, new byte[32], "[]", now.AddDays(90)), default);
            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            await sp.GetRequiredService<IVerificationResultStore>().ReplaceChecksAsync(
                request.Id, [CheckStage.Rules], [VerificationCheck.Create(_a, request.Id, 0, "X", CheckStage.Rules, CheckOutcome.Pass, CheckSeverity.Info, null, "ok")], default);
            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            _requestA = request.Id;
            _documentA = document.Id;
        });
    }

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask();

    [Fact]
    public async Task Another_tenant_cannot_read_by_id_or_list_or_download()
    {
        await _rig.TenantAsync(_b, async sp =>
        {
            var repo = sp.GetRequiredService<IVerificationRepository>();
            (await repo.GetAsync(_requestA, default)).ShouldBeNull();
            (await repo.GetReadOnlyAsync(_requestA, default)).ShouldBeNull();
            (await repo.ListAsync(new VerificationListFilter(), 0, 50, default)).Total.ShouldBe(0);
            (await repo.ListEventsAsync(_requestA, 10, default)).ShouldBeEmpty();
            (await sp.GetRequiredService<IVerificationDocumentStore>().GetCiphertextAsync(_requestA, default)).ShouldBeNull();
            (await sp.GetRequiredService<IVerificationResultStore>().GetResultAsync(_requestA, default)).ShouldBeNull();
            (await sp.GetRequiredService<IVerificationResultStore>().ListChecksAsync(_requestA, default)).ShouldBeEmpty();

            // raw SQL under RLS: no tenant filter in the query at all
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM verification.Requests").SingleAsync()).ShouldBe(0);
            (await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM verification.DocumentBlobs").SingleAsync()).ShouldBe(0);
            (await db.Database.ExecuteSqlRawAsync("UPDATE verification.Requests SET ErrorCode = 'X' WHERE Id = {0}", _requestA)).ShouldBe(0);
            (await db.Database.ExecuteSqlRawAsync("DELETE FROM verification.Documents WHERE Id = {0}", _documentA)).ShouldBe(0);
        });
    }

    [Fact]
    public async Task Owner_can_read_everything_and_ciphertext_is_bound_to_the_request()
    {
        await _rig.TenantAsync(_a, async sp =>
        {
            (await sp.GetRequiredService<IVerificationRepository>().GetReadOnlyAsync(_requestA, default)).ShouldNotBeNull();
            var cipher = await sp.GetRequiredService<IVerificationDocumentStore>().GetCiphertextAsync(_requestA, default);
            cipher!.Content.ShouldBe([1, 2, 3, 4]);
            cipher.DocumentId.ShouldBe(_documentA);
            (await sp.GetRequiredService<IVerificationDocumentStore>().GetCiphertextAsync(Guid.NewGuid(), default)).ShouldBeNull();
            (await sp.GetRequiredService<IVerificationResultStore>().ListChecksAsync(_requestA, default)).Count.ShouldBe(1);
            (await sp.GetRequiredService<IVerificationRepository>().ListEventsAsync(_requestA, 10, default)).Count.ShouldBe(1);
        });
    }

    [Fact]
    public async Task Platform_scope_sees_request_metadata_but_never_the_strict_tables()
    {
        await _rig.PlatformAsync(async sp =>
        {
            (await sp.GetRequiredService<IVerificationRepository>().GetReadOnlyAsync(_requestA, default)).ShouldNotBeNull();
            (await sp.GetRequiredService<IVerificationRepository>().ListEventsAsync(_requestA, 10, default)).Count.ShouldBe(1);
            (await sp.GetRequiredService<IVerificationDocumentStore>().GetCiphertextAsync(_requestA, default)).ShouldBeNull();
            (await sp.GetRequiredService<IVerificationResultStore>().GetResultAsync(_requestA, default)).ShouldBeNull();

            var db = sp.GetRequiredService<AppDbContext>();
            // bypass the EF filter entirely: RLS alone must hide the rows
            (await db.VerificationDocuments.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
            (await db.VerificationDocumentBlobs.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
            (await db.VerificationResults.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
            (await db.VerificationChecks.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
            (await db.VerificationRequests.IgnoreQueryFilters().CountAsync()).ShouldBeGreaterThan(0);
            (await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM verification.Documents").SingleAsync()).ShouldBe(0);
        });
        (await _rig.ScalarAsync<int>("SELECT COUNT(*) FROM verification.Requests")).ShouldBe(0); // no session context at all => nothing
    }

    [Fact]
    public async Task Write_guard_rejects_cross_tenant_and_platform_writes()
    {
        await _rig.TenantAsync(_b, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.VerificationRequests.Add(M14Rig.NewRequest(_a, DateTime.UtcNow));
            await Should.ThrowAsync<TenantViolationException>(() => db.SaveChangesAsync());
        });

        await _rig.PlatformAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.VerificationDocuments.Add(VerificationDocument.Create(_a, _requestA, DocumentKind.Pdf, "application/pdf", "pdf", 1, 1, new byte[32], DateTime.UtcNow));
            await Should.ThrowAsync<TenantViolationException>(() => db.SaveChangesAsync());
        });

        // RLS blocks a raw insert for another tenant even with the write guard out of the way
        await _rig.TenantAsync(_b, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var ex = await Should.ThrowAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "INSERT INTO verification.Events (ClientId, RequestId, ToStatus, ActorType, CreatedAt) VALUES ({0}, {1}, 'Queued', 'System', SYSUTCDATETIME())", _a, _requestA));
            ex.Number.ShouldBe(33504);
        });
    }

    [Theory]
    [InlineData("UPDATE verification.Events SET NoteCode = 'tampered'")]
    [InlineData("DELETE FROM verification.Events")]
    public async Task Events_are_append_only(string sql)
    {
        await _rig.TenantAsync(_a, async sp =>
        {
            var ex = await Should.ThrowAsync<SqlException>(() => sp.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(sql));
            ex.Number.ShouldBe(51000);
        });
    }

    [Fact]
    public async Task Idempotency_key_is_unique_per_tenant_and_replays_return_the_first_request()
    {
        var now = DateTime.UtcNow;
        var first = await _rig.TenantAsync(_a, sp => sp.GetRequiredService<IVerificationRepository>().CreateIdempotentAsync(M14Rig.NewRequest(_a, now, idempotencyKey: "k-1"), null, null, default));
        var replay = await _rig.TenantAsync(_a, sp => sp.GetRequiredService<IVerificationRepository>().CreateIdempotentAsync(M14Rig.NewRequest(_a, now, idempotencyKey: "k-1"), null, null, default));
        var other = await _rig.TenantAsync(_b, sp => sp.GetRequiredService<IVerificationRepository>().CreateIdempotentAsync(M14Rig.NewRequest(_b, now, idempotencyKey: "k-1"), null, null, default));

        first.Created.ShouldBeTrue();
        replay.Created.ShouldBeFalse();
        replay.Request.Id.ShouldBe(first.Request.Id);
        other.Created.ShouldBeTrue();
        other.Request.Id.ShouldNotBe(first.Request.Id);
    }

    [Fact]
    public async Task Concurrent_identical_submits_create_exactly_one_request()
    {
        var now = DateTime.UtcNow;
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
            _rig.TenantAsync(_a, sp => sp.GetRequiredService<IVerificationRepository>().CreateIdempotentAsync(M14Rig.NewRequest(_a, now, idempotencyKey: "race"), null, null, default)))));

        outcomes.Count(o => o.Created).ShouldBe(1);
        outcomes.Select(o => o.Request.Id).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Raw_unique_index_rejects_a_duplicate_key_for_the_same_tenant()
    {
        var now = DateTime.UtcNow;
        await _rig.TenantAsync(_a, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.VerificationRequests.Add(M14Rig.NewRequest(_a, now, idempotencyKey: "dup"));
            await db.SaveChangesAsync();
            db.VerificationRequests.Add(M14Rig.NewRequest(_a, now, idempotencyKey: "dup"));
            await Should.ThrowAsync<UniqueConstraintViolationException>(() => db.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task Concurrent_status_changes_conflict_on_the_row_version()
    {
        var now = DateTime.UtcNow;
        var id = await _rig.TenantAsync(_a, async sp =>
        {
            var r = M14Rig.NewRequest(_a, now);
            sp.GetRequiredService<IVerificationRepository>().Add(r);
            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            return r.Id;
        });

        await _rig.TenantAsync(_a, async sp1 =>
        {
            var one = await sp1.GetRequiredService<IVerificationRepository>().GetAsync(id, default);
            await _rig.TenantAsync(_a, async sp2 =>
            {
                var two = await sp2.GetRequiredService<IVerificationRepository>().GetAsync(id, default);
                two!.Cancel(now);
                (await sp2.GetRequiredService<IVerificationRepository>().TrySaveAsync(default)).ShouldBeTrue();
            });

            one!.Claim(Guid.NewGuid(), now.AddMinutes(1), now);
            (await sp1.GetRequiredService<IVerificationRepository>().TrySaveAsync(default)).ShouldBeFalse();
        });

        await _rig.TenantAsync(_a, async sp => (await sp.GetRequiredService<IVerificationRepository>().GetReadOnlyAsync(id, default))!.Status.ShouldBe(VerificationStatus.Cancelled));
    }

    [Fact]
    public async Task Download_grant_is_consumed_exactly_once_by_its_principal_and_tenant()
    {
        var now = DateTime.UtcNow;
        var principal = Guid.NewGuid();
        var token = RandomNumberGenerator.GetBytes(32);
        var hash = SHA256.HashData(token);
        await _rig.TenantAsync(_a, async sp =>
        {
            sp.GetRequiredService<IVerificationDownloadGrants>().Add(VerificationDownloadGrant.Create(_a, _requestA, DownloadKind.Report, "json", principal, hash, now, TimeSpan.FromSeconds(60)));
            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        });

        (await _rig.TenantAsync(_b, sp => sp.GetRequiredService<IVerificationDownloadGrants>().ConsumeAsync(hash, principal, now, default))).Status.ShouldBe(GrantConsumeStatus.NotFound);
        (await _rig.TenantAsync(_a, sp => sp.GetRequiredService<IVerificationDownloadGrants>().ConsumeAsync(hash, Guid.NewGuid(), now, default))).Status.ShouldBe(GrantConsumeStatus.NotOwner);
        (await _rig.TenantAsync(_a, sp => sp.GetRequiredService<IVerificationDownloadGrants>().ConsumeAsync(hash, principal, now.AddMinutes(5), default))).Status.ShouldBe(GrantConsumeStatus.ExpiredOrUsed);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            _rig.TenantAsync(_a, sp => sp.GetRequiredService<IVerificationDownloadGrants>().ConsumeAsync(hash, principal, now, default)))));
        results.Count(r => r.Status == GrantConsumeStatus.Consumed).ShouldBe(1);
        results.Single(r => r.Status == GrantConsumeStatus.Consumed).Kind.ShouldBe(DownloadKind.Report);
        results.Single(r => r.Status == GrantConsumeStatus.Consumed).RequestId.ShouldBe(_requestA);
        results.Count(r => r.Status == GrantConsumeStatus.ExpiredOrUsed).ShouldBe(7);
    }

    [Fact]
    public async Task Retention_primitives_purge_data_and_keep_metadata()
    {
        var now = DateTime.UtcNow;
        // not due yet
        await _rig.TenantAsync(_a, async sp => (await sp.GetRequiredService<IVerificationRetention>().ListDocumentsDueAsync(now, 10, default)).ShouldBeEmpty());
        await _rig.TenantAsync(_a, async sp => (await sp.GetRequiredService<IVerificationRetention>().ListDocumentsDueAsync(now.AddDays(8), 10, default)).ShouldContain(_requestA));
        await _rig.TenantAsync(_a, async sp => (await sp.GetRequiredService<IVerificationRetention>().ListResultsDueAsync(now.AddDays(91), 10, default)).ShouldContain(_requestA));

        // a platform-scope purge of strict data does nothing (and cannot see it)
        (await _rig.PlatformAsync(sp => sp.GetRequiredService<IVerificationRetention>().PurgeDocumentAsync(_requestA, now, default))).ShouldBeFalse();

        (await _rig.TenantAsync(_b, sp => sp.GetRequiredService<IVerificationRetention>().EraseAsync(_requestA, now, default))).ShouldBeFalse();
        (await _rig.TenantAsync(_a, sp => sp.GetRequiredService<IVerificationRetention>().EraseAsync(_requestA, now, default))).ShouldBeTrue();
        (await _rig.TenantAsync(_a, sp => sp.GetRequiredService<IVerificationRetention>().EraseAsync(_requestA, now, default))).ShouldBeFalse(); // idempotent

        await _rig.TenantAsync(_a, async sp =>
        {
            (await sp.GetRequiredService<IVerificationDocumentStore>().GetCiphertextAsync(_requestA, default)).ShouldBeNull();
            (await sp.GetRequiredService<IVerificationDocumentStore>().GetAsync(_requestA, default))!.PurgedAt.ShouldNotBeNull();
            (await sp.GetRequiredService<IVerificationResultStore>().GetResultAsync(_requestA, default)).ShouldBeNull();
            (await sp.GetRequiredService<IVerificationResultStore>().ListChecksAsync(_requestA, default)).ShouldBeEmpty();
            var request = await sp.GetRequiredService<IVerificationRepository>().GetReadOnlyAsync(_requestA, default);
            request!.PurgedAt.ShouldNotBeNull();
            (await sp.GetRequiredService<IVerificationRepository>().ListEventsAsync(_requestA, 10, default)).Count.ShouldBe(1);
        });
    }

    [Fact]
    public async Task Due_requests_grants_and_ip_blanking()
    {
        var now = DateTime.UtcNow;
        var due = await _rig.TenantAsync(_b, async sp =>
        {
            var r = M14Rig.NewRequest(_b, now.AddDays(-100), expiresAt: now.AddDays(-1));
            sp.GetRequiredService<IVerificationRepository>().Add(r);
            sp.GetRequiredService<IVerificationDownloadGrants>().Add(VerificationDownloadGrant.Create(_b, r.Id, DownloadKind.Report, null, Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), now.AddDays(-3), TimeSpan.FromSeconds(60)));
            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            return r.Id;
        });

        var listed = await _rig.PlatformAsync(sp => sp.GetRequiredService<IVerificationRetention>().ListRequestsDueAsync(now, 100, default));
        listed.ShouldContain(x => x.RequestId == due && x.ClientId == _b);
        listed.ShouldNotContain(x => x.RequestId == _requestA);

        (await _rig.PlatformAsync(sp => sp.GetRequiredService<IVerificationRetention>().PurgeExpiredGrantsAsync(now.AddDays(-1), 100, default))).ShouldBe(1);
        (await _rig.PlatformAsync(sp => sp.GetRequiredService<IVerificationRetention>().BlankIpAddressesAsync(now.AddDays(1), 100, default))).ShouldBeGreaterThanOrEqualTo(1);
        (await _rig.TenantAsync(_b, sp => sp.GetRequiredService<IVerificationRepository>().GetReadOnlyAsync(due, default)))!.IpAddress.ShouldBeNull();
    }
}
