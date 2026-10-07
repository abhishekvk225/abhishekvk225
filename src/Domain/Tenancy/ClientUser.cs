using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Tenancy;

/// <summary>Membership details of a user inside a client (the sign-in identity itself is <c>Identity.User</c>).</summary>
public sealed class ClientUser : AuditableEntity, ITenantOwned
{
    private ClientUser()
    {
    }

    public Guid ClientId { get; set; }

    public Guid UserId { get; private set; }

    public string? JobTitle { get; set; }

    public bool IsOwner { get; private set; }

    public DateTime? InvitedAt { get; set; }

    public DateTime? JoinedAt { get; set; }

    public static ClientUser Create(Guid clientId, Guid userId, string? jobTitle, bool isOwner, DateTime now) =>
        new() { ClientId = clientId, UserId = userId, JobTitle = jobTitle, IsOwner = isOwner, InvitedAt = now };
}
