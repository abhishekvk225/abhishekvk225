using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Abstractions;
using NexaVerify.Domain.Common;

namespace NexaVerify.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context. Module entity configurations are discovered from this assembly; every
/// <see cref="ITenantOwned"/> entity automatically receives the tenant query filter (named "Tenant").
/// Subclasses (tests, future split contexts) may add DbSets and configuration.
/// </summary>
public class AppDbContext : DbContext, IUnitOfWork
{
    public const string TenantFilterName = "Tenant";

    private static readonly MethodInfo ApplyTenantFilterMethod =
        typeof(AppDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo ApplyStrictTenantFilterMethod =
        typeof(AppDbContext).GetMethod(nameof(ApplyStrictTenantFilter), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly ITenantContext _tenant;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant)
        : this((DbContextOptions)options, tenant)
    {
    }

    protected AppDbContext(DbContextOptions options, ITenantContext tenant)
        : base(options)
    {
        _tenant = tenant;
    }

    // Read by the compiled query filters at query time (EF re-evaluates them per context instance).
    private bool FilterIsPlatform => _tenant.IsPlatform;

    private Guid? FilterClientId => _tenant.ClientId;

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        // Wrap in the execution strategy so retry policies (if enabled) re-run the whole unit of work.
        var strategy = Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
            var result = await action(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(e => !e.IsOwned()).ToList())
        {
            var clrType = entityType.ClrType;
            if (typeof(Entity).IsAssignableFrom(clrType))
            {
                entityType.FindProperty(nameof(Entity.Id))?.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }

            if (!typeof(ITenantOwned).IsAssignableFrom(clrType) || entityType.BaseType is not null)
            {
                continue;
            }

            var method = typeof(IStrictTenantOwned).IsAssignableFrom(clrType)
                ? ApplyStrictTenantFilterMethod
                : ApplyTenantFilterMethod;
            method.MakeGenericMethod(clrType).Invoke(this, [modelBuilder]);
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums are stored as readable strings (CHECK constraints are added per entity configuration).
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(30);
        configurationBuilder.Properties<DateTime>().HavePrecision(3).HaveColumnType("datetime2(3)");
        configurationBuilder.Properties<string>().HaveMaxLength(256);
    }

    // Platform scope may read all tenants; a tenant sees only its own rows; no tenant => no rows (fail-closed).
    private void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantOwned
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(
            TenantFilterName,
            e => FilterIsPlatform || e.ClientId == FilterClientId);
    }

    // Strict entities (biometrics): platform scope gets no cross-tenant access either.
    private void ApplyStrictTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, IStrictTenantOwned
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(
            TenantFilterName,
            e => e.ClientId == FilterClientId);
    }
}
