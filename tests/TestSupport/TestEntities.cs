using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Abstractions;
using NexaVerify.Domain.Common;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.TestSupport;

/// <summary>Tenant-owned test entity (platform scope may read across tenants).</summary>
public sealed class Widget : AuditableEntity, ITenantOwned
{
    public Guid ClientId { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>Strict tenant-owned test entity (platform scope must not read across tenants).</summary>
public sealed class SecretWidget : AuditableEntity, IStrictTenantOwned
{
    public Guid ClientId { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>Not tenant-owned: must be unaffected by tenant machinery.</summary>
public sealed class GlobalThing : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
}

public sealed class TestDbContext : AppDbContext
{
    public TestDbContext(DbContextOptions<TestDbContext> options, ITenantContext tenant)
        : base(options, tenant)
    {
    }

    public DbSet<Widget> Widgets => Set<Widget>();

    public DbSet<SecretWidget> SecretWidgets => Set<SecretWidget>();

    public DbSet<GlobalThing> GlobalThings => Set<GlobalThing>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Widget>().ToTable("Widgets", "test");
        modelBuilder.Entity<SecretWidget>().ToTable("SecretWidgets", "test");
        modelBuilder.Entity<GlobalThing>().ToTable("GlobalThings", "test");
        base.OnModelCreating(modelBuilder);
    }
}
