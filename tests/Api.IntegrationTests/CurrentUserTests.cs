using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NexaVerify.Api.Http;
using NexaVerify.Application.Abstractions;
using NexaVerify.Contracts.Common;

namespace NexaVerify.Api.IntegrationTests;

public class CurrentUserTests
{
    private static HttpCurrentUser For(params Claim[] claims)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
        };
        return new HttpCurrentUser(new HttpContextAccessor { HttpContext = context });
    }

    private static Claim Sub(Guid id) => new(NexaClaims.Subject, id.ToString());

    private static Claim Cid(Guid id) => new(NexaClaims.ClientId, id.ToString());

    private static Claim Actor(string value) => new(NexaClaims.ActorType, value);

    [Fact]
    public void Tenant_user_resolves_client_and_actor()
    {
        var user = Guid.NewGuid();
        var client = Guid.NewGuid();

        var current = For(Sub(user), Cid(client));

        current.IsAuthenticated.ShouldBeTrue();
        current.ActorId.ShouldBe(user);
        current.ClientId.ShouldBe(client);
        current.IsPlatformUser.ShouldBeFalse();
        current.ActorType.ShouldBe(ActorType.User);
    }

    [Fact]
    public void Platform_user_has_no_client()
    {
        var current = For(Sub(Guid.NewGuid()), Actor(NexaClaims.PlatformActor));

        current.IsPlatformUser.ShouldBeTrue();
        current.ClientId.ShouldBeNull();
    }

    [Fact]
    public void Api_key_actor_is_recognised()
    {
        For(Sub(Guid.NewGuid()), Cid(Guid.NewGuid()), Actor(NexaClaims.ApiKeyActor)).ActorType.ShouldBe(ActorType.ApiKey);
    }

    [Fact]
    public void A_platform_actor_that_also_has_a_client_is_treated_as_unauthenticated()
    {
        var current = For(Sub(Guid.NewGuid()), Cid(Guid.NewGuid()), Actor(NexaClaims.PlatformActor));

        current.IsAuthenticated.ShouldBeFalse();
        current.IsPlatformUser.ShouldBeFalse();
        current.ClientId.ShouldBeNull();
    }

    [Fact]
    public void Several_actor_claims_cannot_promote_an_api_key_to_platform()
    {
        var current = For(Sub(Guid.NewGuid()), Cid(Guid.NewGuid()), Actor(NexaClaims.ApiKeyActor), Actor(NexaClaims.PlatformActor));

        current.IsAuthenticated.ShouldBeFalse();
        current.IsPlatformUser.ShouldBeFalse();
    }

    [Fact]
    public void Several_client_claims_or_a_malformed_client_id_fail_closed()
    {
        For(Sub(Guid.NewGuid()), Cid(Guid.NewGuid()), Cid(Guid.NewGuid())).IsAuthenticated.ShouldBeFalse();
        For(Sub(Guid.NewGuid()), new Claim(NexaClaims.ClientId, "not-a-guid")).IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public void Anonymous_requests_are_anonymous()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var current = new HttpCurrentUser(accessor);

        current.IsAuthenticated.ShouldBeFalse();
        current.ActorType.ShouldBe(ActorType.Anonymous);
    }
}
