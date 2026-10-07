namespace NexaVerify.Domain.Common;

/// <summary>
/// Marks an entity as belonging to exactly one client (tenant). Persistence applies a global query
/// filter, a write guard and SQL Server row-level security to every implementor.
/// Platform (Super Admin) scope may read these rows across tenants.
/// </summary>
public interface ITenantOwned
{
    Guid ClientId { get; set; }
}

/// <summary>
/// Tenant-owned data that even platform scope must not read across tenants (e.g. biometric data).
/// Only the owning tenant can see these rows.
/// </summary>
public interface IStrictTenantOwned : ITenantOwned
{
}
