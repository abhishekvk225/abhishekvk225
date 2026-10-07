using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NexaVerify.Application.Abstractions;
using NexaVerify.Infrastructure.Persistence.Interceptors;
using NexaVerify.Infrastructure.Persistence.Rls;
using NexaVerify.Infrastructure.Tenancy;

namespace NexaVerify.TestSupport;

public sealed class StubCurrentUser : ICurrentUser
{
    public bool IsAuthenticated { get; set; }

    public ActorType ActorType { get; set; } = ActorType.Anonymous;

    public Guid? ActorId { get; set; }

    public Guid? ClientId { get; set; }

    public bool IsPlatformUser { get; set; }
}

/// <summary>A fresh database with the test schema and row-level security applied, plus factories for tenant-aware contexts.</summary>
public sealed class TestDb
{
    private TestDb(string connectionString)
    {
        ConnectionString = connectionString;
        User = new StubCurrentUser();
        Tenant = new TenantContext(User);
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero));
    }

    public string ConnectionString { get; }

    public StubCurrentUser User { get; }

    public TenantContext Tenant { get; }

    public FakeTimeProvider Time { get; }

    public static async Task<TestDb> CreateAsync(SqlServerFixture fixture)
    {
        var db = new TestDb(await fixture.CreateDatabaseAsync());
        using (db.Tenant.BeginPlatform())
        {
            await using var context = db.NewContext();
            await context.Database.EnsureCreatedAsync();
            await new RowLevelSecurityInstaller(db.Tenant).InstallAsync(context);
        }

        return db;
    }

    public TestDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServer(ConnectionString)
            .AddInterceptors(
                new AuditAndTenantSaveChangesInterceptor(Tenant, User, Time),
                new TenantSessionContextInterceptor(Tenant))
            .Options;
        return new TestDbContext(options, Tenant);
    }
}
