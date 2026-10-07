namespace NexaVerify.Domain.Common;

/// <summary>
/// The entity that IS the tenant (the client record). Its own <c>Id</c> plays the role that <c>ClientId</c> plays on
/// tenant-owned rows: a tenant sees only its own record, platform scope sees all. Enforced by the EF filter, the write
/// guard and row-level security.
/// </summary>
public interface ITenantRoot
{
    Guid Id { get; }
}
