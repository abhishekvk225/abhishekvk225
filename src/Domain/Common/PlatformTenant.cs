namespace NexaVerify.Domain.Common;

/// <summary>
/// Well-known tenant id that owns platform-level rows (platform staff accounts, platform audit events,
/// failed logins for unknown emails). Only platform scope can see rows owned by it. A matching system
/// client row is seeded by the tenancy module.
/// </summary>
public static class PlatformTenant
{
    public static readonly Guid ClientId = new("00000000-0000-0000-0000-00000000F001");
}
