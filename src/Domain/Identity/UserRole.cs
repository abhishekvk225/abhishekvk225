using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Identity;

/// <summary>Role assignment. Carries the user's ClientId so it is isolated like every other tenant row.</summary>
public sealed class UserRole : ITenantOwned
{
    public Guid ClientId { get; set; }

    public Guid UserId { get; set; }

    public Guid RoleId { get; set; }
}
