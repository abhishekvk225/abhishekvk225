using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NexaVerify.Application.Abstractions;
using NexaVerify.Infrastructure.Persistence.Interceptors;
using NexaVerify.Infrastructure.Persistence.Guards;
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

    public IReadOnlyCollection<string> Roles { get; set; } = [];
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
        using (db.Tenant.BeginPlatform("test setup"))
        {
            await using var context = db.NewContext();
            await context.Database.EnsureCreatedAsync();
            await new DatabaseGuardsInstaller(new RowLevelSecurityInstaller(db.Tenant)).InstallAsync(context);
        }

        return db;
    }

    /// <summary>Inserts a client row with the given id (tables with foreign keys to Clients need a real tenant).</summary>
    public async Task EnsureClientAsync(Guid clientId)
    {
        using var scope = Tenant.BeginPlatform("test: ensure client");
        await using var context = NewContext();
        if (await context.Clients.AnyAsync(c => c.Id == clientId))
        {
            return;
        }

        var client = NexaVerify.Domain.Tenancy.Client.Create("T" + clientId.ToString("N")[..12], "Test " + clientId.ToString("N")[..6], "ops@test.invalid", "UTC", Time.GetUtcNow().UtcDateTime);
        typeof(NexaVerify.Domain.Common.Entity).GetProperty(nameof(NexaVerify.Domain.Common.Entity.Id))!.SetValue(client, clientId);
        context.Clients.Add(client);
        await context.SaveChangesAsync();
    }

    public TestDbContext NewContext()
    {
        var applier = new TenantSessionContextApplier(Tenant);
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServer(ConnectionString)
            .AddInterceptors(
                new AuditAndTenantSaveChangesInterceptor(Tenant, User, Time),
                new TenantSessionContextInterceptor(applier),
                new TenantSessionContextCommandInterceptor(applier))
            .Options;
        return new TestDbContext(options, Tenant);
    }
}
