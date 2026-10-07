using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NexaVerify.Application.Abstractions;
using NexaVerify.Domain.Common;
using NexaVerify.Application.Common;

namespace NexaVerify.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Runs before every save: (1) stamps audit columns, (2) stamps ClientId on new tenant-owned rows and
/// (3) acts as the *write guard* — rejects any add/modify/delete of a tenant-owned row outside the
/// current tenant, and any attempt to reassign a row to another tenant.
/// </summary>
public sealed class AuditAndTenantSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly ITenantContext _tenant;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _time;

    public AuditAndTenantSaveChangesInterceptor(ITenantContext tenant, ICurrentUser currentUser, TimeProvider time)
    {
        _tenant = tenant;
        _currentUser = currentUser;
        _time = time;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var actor = _currentUser.ActorId;

        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                if (entry.Entity is ITenantRoot root)
                {
                    GuardTenantRoot(entry, root);
                }
                else if (entry.Entity is ITenantOwned owned)
                {
                    GuardTenant(entry, owned);
                }
            }

            if (entry.Entity is not AuditableEntity auditable)
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    auditable.CreatedAt = now;
                    auditable.CreatedBy = actor;
                    break;
                case EntityState.Modified:
                    // Created* are immutable once written.
                    entry.Property(nameof(AuditableEntity.CreatedAt)).IsModified = false;
                    entry.Property(nameof(AuditableEntity.CreatedBy)).IsModified = false;
                    auditable.UpdatedAt = now;
                    auditable.UpdatedBy = actor;
                    break;
            }
        }
    }

    /// <summary>The client record: only platform scope may create or delete it; a tenant may edit just its own.</summary>
    private void GuardTenantRoot(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, ITenantRoot root)
    {
        if (_tenant.IsPlatform)
        {
            return;
        }

        var name = entry.Metadata.ClrType.Name;
        if (entry.State != EntityState.Modified)
        {
            throw new TenantViolationException($"Only platform scope can add or delete {name}.");
        }

        if (_tenant.ClientId != root.Id)
        {
            throw new TenantViolationException($"Cannot modify {name} of a different tenant.");
        }
    }

    private void GuardTenant(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, ITenantOwned owned)
    {
        var entityName = entry.Metadata.ClrType.Name;

        // Strict (e.g. biometric) rows: platform scope may never write them, mirroring the database policy.
        if (owned is IStrictTenantOwned && _tenant.IsPlatform)
        {
            throw new TenantViolationException($"Platform scope cannot write {entityName}.");
        }

        if (entry.State == EntityState.Added)
        {
            if (_tenant.IsPlatform)
            {
                if (owned.ClientId == Guid.Empty)
                {
                    throw new TenantViolationException(
                        $"Platform scope must set ClientId explicitly when adding {entityName}.");
                }

                return;
            }

            if (_tenant.ClientId is not { } current)
            {
                throw new TenantViolationException($"No tenant is in scope; cannot add {entityName}.");
            }

            if (owned.ClientId == Guid.Empty)
            {
                owned.ClientId = current;
            }
            else if (owned.ClientId != current)
            {
                throw new TenantViolationException($"Cannot add {entityName} for a different tenant.");
            }

            return;
        }

        // Modified / Deleted
        var clientIdProperty = entry.Property(nameof(ITenantOwned.ClientId));
        if (entry.State == EntityState.Modified && clientIdProperty.IsModified)
        {
            throw new TenantViolationException($"ClientId of {entityName} is immutable.");
        }

        if (_tenant.IsPlatform)
        {
            return;
        }

        if (_tenant.ClientId is not { } tenantId || owned.ClientId != tenantId)
        {
            throw new TenantViolationException($"Cannot modify or delete {entityName} of a different tenant.");
        }
    }
}
