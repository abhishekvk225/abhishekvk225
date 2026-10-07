using NexaVerify.Application.Abstractions;

namespace NexaVerify.Infrastructure.Tenancy;

/// <summary>Fallback principal for code running outside an HTTP request (no tenant, no actor).</summary>
public sealed class AnonymousCurrentUser : ICurrentUser
{
    public bool IsAuthenticated => false;

    public ActorType ActorType => ActorType.Anonymous;

    public Guid? ActorId => null;

    public Guid? ClientId => null;

    public bool IsPlatformUser => false;

    public IReadOnlyCollection<string> Roles => [];
}
