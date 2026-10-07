namespace NexaVerify.Application.Abstractions;

public enum ActorType
{
    Anonymous = 0,
    User,
    ApiKey,
    System,
}

/// <summary>The authenticated principal of the current request, abstracted away from HttpContext.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    ActorType ActorType { get; }

    /// <summary>User id (for <see cref="ActorType.User"/>) or API key id (for <see cref="ActorType.ApiKey"/>).</summary>
    Guid? ActorId { get; }

    /// <summary>Client the principal belongs to; null for platform users.</summary>
    Guid? ClientId { get; }

    bool IsPlatformUser { get; }
}
