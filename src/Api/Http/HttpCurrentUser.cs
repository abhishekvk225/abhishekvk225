using System.Security.Claims;
using NexaVerify.Application.Abstractions;
using NexaVerify.Contracts.Common;

namespace NexaVerify.Api.Http;

/// <summary>Maps the authenticated <see cref="ClaimsPrincipal"/> to <see cref="ICurrentUser"/>. The only place that reads tenant claims.</summary>
public sealed class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public HttpCurrentUser(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public ActorType ActorType
    {
        get
        {
            if (!IsAuthenticated)
            {
                return ActorType.Anonymous;
            }

            return Principal!.FindFirstValue(NexaClaims.ActorType) switch
            {
                "apikey" => ActorType.ApiKey,
                _ => ActorType.User,
            };
        }
    }

    public Guid? ActorId => IsAuthenticated ? ParseGuid(Principal!.FindFirstValue(NexaClaims.Subject)) : null;

    public Guid? ClientId => IsAuthenticated ? ParseGuid(Principal!.FindFirstValue(NexaClaims.ClientId)) : null;

    // A platform user is authenticated and carries no client binding; an unbound principal is never promoted by default.
    public bool IsPlatformUser => IsAuthenticated && Principal!.HasClaim(NexaClaims.ActorType, NexaClaims.PlatformActor);

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var id) ? id : null;
}
