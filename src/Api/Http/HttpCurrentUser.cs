using System.Security.Claims;
using NexaVerify.Application.Abstractions;
using NexaVerify.Contracts.Common;

namespace NexaVerify.Api.Http;

/// <summary>
/// Maps the authenticated <see cref="ClaimsPrincipal"/> to <see cref="ICurrentUser"/>. The only place that reads tenant claims.
/// Fail-closed: a principal with ambiguous or contradictory identity claims (several actor claims, or a platform actor
/// that also carries a client id) is treated as unauthenticated so it can never be promoted to platform scope.
/// </summary>
public sealed class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;
    private ClaimsPrincipal? _resolvedFor;
    private Identity? _resolved;

    public HttpCurrentUser(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public bool IsAuthenticated => Resolve() is not null;

    public ActorType ActorType => Resolve() is { } identity ? identity.Actor : ActorType.Anonymous;

    public Guid? ActorId => Resolve()?.ActorId;

    public Guid? ClientId => Resolve()?.ClientId;

    public bool IsPlatformUser => Resolve()?.IsPlatform == true;

    public IReadOnlyCollection<string> Roles => Resolve()?.Roles ?? [];

    private Identity? Resolve()
    {
        var principal = Principal;
        if (!ReferenceEquals(principal, _resolvedFor))
        {
            _resolved = Parse(principal);
            _resolvedFor = principal;
        }

        return _resolved;
    }

    private static Identity? Parse(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var actors = principal.FindAll(NexaClaims.ActorType).Select(c => c.Value).ToList();
        var clients = principal.FindAll(NexaClaims.ClientId).Select(c => c.Value).ToList();
        var subjects = principal.FindAll(NexaClaims.Subject).Select(c => c.Value).ToList();

        if (actors.Count > 1 || clients.Count > 1 || subjects.Count > 1)
        {
            return null; // ambiguous identity
        }

        var isPlatform = actors is [NexaClaims.PlatformActor];
        if (isPlatform && clients.Count > 0)
        {
            return null; // platform principals are never bound to a client
        }

        // A client principal must carry exactly one valid, real client id; anything else is not a usable identity.
        if (!isPlatform && (clients.Count != 1 || !Guid.TryParse(clients[0], out var parsedClient)
            || parsedClient == Guid.Empty || parsedClient == NexaVerify.Domain.Common.PlatformTenant.ClientId))
        {
            return null;
        }

        var actor = actors is [NexaClaims.ApiKeyActor] ? ActorType.ApiKey : ActorType.User;
        Guid? clientId = clients.Count == 1 ? Guid.Parse(clients[0]) : null;
        Guid? actorId = subjects.Count == 1 && Guid.TryParse(subjects[0], out var id) ? id : null;

        var roles = principal.FindAll(NexaClaims.Role).Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList();
        return new Identity(actor, actorId, clientId, isPlatform, roles);
    }

    private sealed record Identity(ActorType Actor, Guid? ActorId, Guid? ClientId, bool IsPlatform, IReadOnlyCollection<string> Roles);
}
