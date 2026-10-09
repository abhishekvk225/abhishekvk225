using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Application;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Public;
using NexaVerify.Domain.Identity;
using NexaVerify.Infrastructure;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.Infrastructure.Persistence.Seed;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>Storage-level behaviour of the public sign-up tables: upgrade path, seeding, and the once-only claim under concurrency.</summary>
[Collection(SqlServerCollection.Name)]
public class PublicSignupStorageTests
{
    private readonly SqlServerFixture _fixture;

    public PublicSignupStorageTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private static ServiceProvider Provider(string connectionString)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(DatabaseBootstrap.BaseSettings(connectionString)).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddApplication();
        services.AddInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Upgrading_an_existing_database_publishes_its_trial_plan_and_keeps_other_plans_private()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();
        try
        {
            await using var provider = Provider(connectionString);
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("test: upgrade");

            // a database as it was before the public website: migrated up to M9b, with the old seeded plans
            await db.GetService<IMigrator>().MigrateAsync("M9bScalability");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO licensing.Plans (Id, Code, Name, DefaultCredits, DefaultDurationDays, RateLimitPerMinute, MaxApiKeys, MaxUsers, CreatedAt, IsActive)
                VALUES (NEWID(), 'TRIAL', 'Trial', 500, 14, 30, 2, 3, SYSUTCDATETIME(), 1), (NEWID(), 'STARTER', 'Starter', 10000, 365, 60, 5, 10, SYSUTCDATETIME(), 1)
                """);

            await db.GetService<IMigrator>().MigrateAsync();

            var plans = await db.Plans.AsNoTracking().OrderBy(p => p.Code).ToListAsync();
            plans.Select(p => (p.Code, p.IsPublic, p.IsTrial)).ShouldBe([("STARTER", false, false), ("TRIAL", true, true)]);
            plans.Single(p => p.Code == "TRIAL").Highlights.ShouldNotBeEmpty();
            plans.Single(p => p.Code == "STARTER").Highlights.ShouldBeEmpty();
            plans.Single(p => p.Code == "TRIAL").Name.ShouldBe("Trial"); // operators' wording is never overwritten
        }
        finally
        {
            await SqlServerFixture.TryDropDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task The_seeder_restores_a_missing_trial_plan_once_and_respects_an_operator_who_unflagged_it()
    {
        await using var app = await AuthApp.CreateAsync(_fixture);
        async Task SeedAsync() => await app.WithServicesAsync(null, async sp =>
        {
            await sp.GetRequiredService<LicensingSeeder>().SeedAsync(default);
            return 0;
        });

        // fresh database: exactly one public trial plan
        (await app.WithDbAsync(db => db.Plans.Where(p => p.IsTrial && p.IsPublic).Select(p => p.Code).ToListAsync())).ShouldBe(["TRIAL"]);

        // the trial plan vanished (other plans remain): the seeder brings it back, and a second run adds nothing
        await app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DELETE FROM licensing.Plans WHERE Code = 'TRIAL'"));
        await SeedAsync();
        await SeedAsync();
        (await app.WithDbAsync(db => db.Plans.CountAsync(p => p.Code == "TRIAL"))).ShouldBe(1);
        (await app.WithDbAsync(db => db.Plans.CountAsync(p => p.IsTrial && p.IsPublic))).ShouldBe(1);

        // an operator who un-flags it (no plan is a trial any more) does not get a second TRIAL plan created
        await app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE licensing.Plans SET IsTrial = 0, IsPublic = 0 WHERE Code = 'TRIAL'"));
        await SeedAsync();
        (await app.WithDbAsync(db => db.Plans.CountAsync(p => p.Code == "TRIAL"))).ShouldBe(1);
        (await app.WithDbAsync(db => db.Plans.CountAsync(p => p.IsTrial))).ShouldBe(0);
    }

    [Fact]
    public async Task The_claim_succeeds_for_exactly_one_of_many_concurrent_callers_and_erases_the_password_hash()
    {
        await using var app = await AuthApp.CreateAsync(_fixture);
        var tokenHash = System.Security.Cryptography.SHA256.HashData("token"u8);
        var id = await app.WithDbAsync(async db =>
        {
            var pending = PendingSignup.Issue("Acme", "Ada", "ada@acme.test", "pbkdf2-hash", tokenHash, DateTime.UtcNow, TimeSpan.FromHours(1));
            db.PendingSignups.Add(pending);
            await db.SaveChangesAsync();
            return pending.Id;
        });

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => app.WithServicesAsync(null, sp =>
            sp.GetRequiredService<IPendingSignupAtomics>().TryClaimAsync(id, tokenHash, DateTime.UtcNow, default))));

        results.Count(r => r).ShouldBe(1);
        var row = await app.WithDbAsync(db => db.PendingSignups.AsNoTracking().SingleAsync());
        row.ConsumedAt.ShouldNotBeNull();
        row.PasswordHash.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_claim_refuses_a_wrong_token_an_expired_row_and_a_consumed_row()
    {
        await using var app = await AuthApp.CreateAsync(_fixture);
        var good = System.Security.Cryptography.SHA256.HashData("good"u8);
        var now = DateTime.UtcNow;
        var (live, expired) = await app.WithDbAsync(async db =>
        {
            var a = PendingSignup.Issue("A", "A", "a@acme.test", "h", good, now, TimeSpan.FromHours(1));
            var b = PendingSignup.Issue("B", "B", "b@acme.test", "h", good, now.AddHours(-3), TimeSpan.FromHours(1));
            db.PendingSignups.AddRange(a, b);
            await db.SaveChangesAsync();
            return (a.Id, b.Id);
        });
        Task<bool> ClaimAsync(Guid id, byte[] hash) =>
            app.WithServicesAsync(null, sp => sp.GetRequiredService<IPendingSignupAtomics>().TryClaimAsync(id, hash, now, default));

        (await ClaimAsync(live, System.Security.Cryptography.SHA256.HashData("bad"u8))).ShouldBeFalse();
        (await ClaimAsync(expired, good)).ShouldBeFalse();
        (await ClaimAsync(live, good)).ShouldBeTrue();
        (await ClaimAsync(live, good)).ShouldBeFalse();
    }

    [Fact]
    public async Task Only_one_live_pending_signup_can_exist_per_address_but_consumed_rows_do_not_block_a_new_one()
    {
        await using var app = await AuthApp.CreateAsync(_fixture);
        var hash = System.Security.Cryptography.SHA256.HashData("t"u8);
        await app.WithDbAsync(async db =>
        {
            db.PendingSignups.Add(PendingSignup.Issue("A", "A", "a@acme.test", "h", hash, DateTime.UtcNow, TimeSpan.FromHours(1)));
            await db.SaveChangesAsync();
            return 0;
        });

        await Should.ThrowAsync<NexaVerify.Application.Common.UniqueConstraintViolationException>(() => app.WithDbAsync(async db =>
        {
            db.PendingSignups.Add(PendingSignup.Issue("A2", "A", "A@ACME.TEST", "h", hash, DateTime.UtcNow, TimeSpan.FromHours(1)));
            await db.SaveChangesAsync();
            return 0;
        }));

        await app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE iam.PendingSignups SET ConsumedAt = SYSUTCDATETIME()"));
        await app.WithDbAsync(async db =>
        {
            db.PendingSignups.Add(PendingSignup.Issue("A3", "A", "a@acme.test", "h", hash, DateTime.UtcNow, TimeSpan.FromHours(1)));
            await db.SaveChangesAsync();
            return 0;
        });
        (await app.WithDbAsync(db => db.PendingSignups.CountAsync())).ShouldBe(2);
    }
}
