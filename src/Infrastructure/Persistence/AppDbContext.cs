using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Common;
using NexaVerify.Domain.Auditing;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Identity;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context. Module entity configurations are discovered from this assembly; every
/// <see cref="ITenantOwned"/> entity automatically receives the tenant query filter (named "Tenant") and every
/// enum column a CHECK constraint. Use one context per tenant scope (the default scoped lifetime) — tracked entities
/// are not re-filtered when the scope changes. Do not use context pooling/factories: the interceptors are scoped.
/// </summary>
public class AppDbContext : DbContext, IUnitOfWork
{
    public const string TenantFilterName = "Tenant";

    private static readonly MethodInfo ApplyTenantFilterMethod =
        typeof(AppDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo ApplyTenantRootFilterMethod =
        typeof(AppDbContext).GetMethod(nameof(ApplyTenantRootFilter), BindingFlags.Instance | BindingFlags.NonPublic)!;

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

    public DbSet<Client> Clients => Set<Client>();

    public DbSet<ClientSetting> ClientSettings => Set<ClientSetting>();

    public DbSet<ClientKey> ClientKeys => Set<ClientKey>();

    public DbSet<ClientUser> ClientUsers => Set<ClientUser>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<UserRole> UserRoles => Set<UserRole>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();

    public DbSet<PendingSignup> PendingSignups => Set<PendingSignup>();

    public DbSet<ContactRequest> ContactRequests => Set<ContactRequest>();

    public DbSet<LoginHistory> LoginHistory => Set<LoginHistory>();

    public DbSet<UserMfa> UserMfa => Set<UserMfa>();

    public DbSet<MfaRecoveryCode> MfaRecoveryCodes => Set<MfaRecoveryCode>();

    public DbSet<MfaChallenge> MfaChallenges => Set<MfaChallenge>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<Plan> Plans => Set<Plan>();

    public DbSet<License> Licenses => Set<License>();

    public DbSet<CostRule> CostRules => Set<CostRule>();

    public DbSet<ClientCostRule> ClientCostRules => Set<ClientCostRule>();

    public DbSet<LicenseTransaction> LicenseTransactions => Set<LicenseTransaction>();

    public DbSet<LicenseAlert> LicenseAlerts => Set<LicenseAlert>();

    public DbSet<LicenseAdjustmentRequest> LicenseAdjustmentRequests => Set<LicenseAdjustmentRequest>();

    public DbSet<LedgerCheckpoint> LedgerCheckpoints => Set<LedgerCheckpoint>();

    public DbSet<LedgerBreakRecord> LedgerBreakRecords => Set<LedgerBreakRecord>();

    public DbSet<LedgerVerificationRun> LedgerVerificationRuns => Set<LedgerVerificationRun>();

    public DbSet<Domain.Api.ApiKey> ApiKeys => Set<Domain.Api.ApiKey>();

    public DbSet<Domain.Api.ApiRequestLog> ApiRequestLogs => Set<Domain.Api.ApiRequestLog>();

    public DbSet<Domain.Api.UsageCounter> UsageCounters => Set<Domain.Api.UsageCounter>();

    public DbSet<Domain.Api.WebhookEndpoint> WebhookEndpoints => Set<Domain.Api.WebhookEndpoint>();

    public DbSet<Domain.Api.WebhookDelivery> WebhookDeliveries => Set<Domain.Api.WebhookDelivery>();

    public DbSet<Domain.Faces.FaceProfile> FaceProfiles => Set<Domain.Faces.FaceProfile>();

    public DbSet<Domain.Faces.FaceTemplate> FaceTemplates => Set<Domain.Faces.FaceTemplate>();

    public DbSet<Domain.Faces.RecognitionRequest> RecognitionRequests => Set<Domain.Faces.RecognitionRequest>();

    public DbSet<Domain.Faces.MatchResult> MatchResults => Set<Domain.Faces.MatchResult>();

    // Read by the compiled query filters at query time (EF re-evaluates them per context instance).
    private bool FilterIsPlatform => _tenant.IsPlatform;

    private Guid? FilterClientId => _tenant.ClientId;

    /// <summary>
    /// Runs <paramref name="action"/> in one database transaction (joins an existing one). The commit itself is never
    /// cancelled mid-flight so billing/ledger writes are all-or-nothing. If a retrying execution strategy is ever
    /// enabled, the delegate must be idempotent.
    /// </summary>
    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        if (Database.CurrentTransaction is not null)
        {
            return await action(cancellationToken);
        }

        var strategy = Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            async token =>
            {
                await using var transaction = await Database.BeginTransactionAsync(token);
                var result = await action(token);
                await transaction.CommitAsync(CancellationToken.None);
                return result;
            },
            cancellationToken);
    }

    public void ClearTracked() => ChangeTracker.Clear();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        try
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        catch (Exception ex) when (Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (Exception ex) when (Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            var clrType = entityType.ClrType;
            if (typeof(Entity).IsAssignableFrom(clrType))
            {
                entityType.FindProperty(nameof(Entity.Id))?.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }

            AddEnumCheckConstraints(modelBuilder, entityType);

            if (entityType.IsOwned() || entityType.BaseType is not null)
            {
                continue;
            }

            if (typeof(ITenantRoot).IsAssignableFrom(clrType))
            {
                ApplyTenantRootFilterMethod.MakeGenericMethod(clrType).Invoke(this, [modelBuilder]);
                continue;
            }

            if (!typeof(ITenantOwned).IsAssignableFrom(clrType))
            {
                continue;
            }

            var method = typeof(IStrictTenantOwned).IsAssignableFrom(clrType)
                ? ApplyStrictTenantFilterMethod
                : ApplyTenantFilterMethod;
            method.MakeGenericMethod(clrType).Invoke(this, [modelBuilder]);
        }

        TenantModelRules.EnsureValid(modelBuilder.Model);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums are stored as readable strings; CHECK constraints are generated in OnModelCreating.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(30);

        // All timestamps are UTC DateTime with millisecond precision (datetime2(3)). Entities must not use DateTimeOffset.
        configurationBuilder.Properties<DateTime>().HavePrecision(3);

        // Safe default; long text columns must opt out explicitly (HasColumnType("nvarchar(max)")).
        configurationBuilder.Properties<string>().HaveMaxLength(256);
    }

    private static Exception? Translate(Exception ex) => ex switch
    {
        DbUpdateConcurrencyException => new ConcurrencyConflictException(ex),
        DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } } => new UniqueConstraintViolationException(ex),
        _ => null,
    };

    private static void AddEnumCheckConstraints(ModelBuilder modelBuilder, Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType entityType)
    {
        if (entityType.IsOwned() || entityType.BaseType is not null || entityType.GetTableName() is not { } table)
        {
            return;
        }

        foreach (var property in entityType.GetProperties())
        {
            var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            if (!type.IsEnum || property.IsShadowProperty())
            {
                continue;
            }

            var allowed = string.Join(", ", Enum.GetNames(type).Select(n => $"'{n}'"));
            var column = property.Name;
            modelBuilder.Entity(entityType.ClrType).ToTable(t =>
                t.HasCheckConstraint($"CK_{table}_{property.Name}", $"[{column}] IN ({allowed})"));
        }
    }

    // Platform scope may read all tenants; a tenant sees only its own rows; no tenant => no rows (fail-closed).
    private void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantOwned
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(
            TenantFilterName,
            e => FilterIsPlatform || e.ClientId == FilterClientId);
    }

    // The client record itself: a tenant sees only its own record (Id), platform scope sees all.
    private void ApplyTenantRootFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantRoot
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(
            TenantFilterName,
            e => FilterIsPlatform || e.Id == FilterClientId);
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
