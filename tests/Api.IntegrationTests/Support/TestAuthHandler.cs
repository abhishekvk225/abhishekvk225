using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using NexaVerify.Contracts.Common;

namespace NexaVerify.Api.IntegrationTests.Support;

/// <summary>Authenticates from headers: X-Test-Client = client guid, X-Test-Platform = any value. No header = anonymous.</summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string ClientHeader = "X-Test-Client";
    public const string PlatformHeader = "X-Test-Platform";

    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var hasClient = Request.Headers.TryGetValue(ClientHeader, out var client);
        var isPlatform = Request.Headers.ContainsKey(PlatformHeader);
        if (!hasClient && !isPlatform)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(NexaClaims.Subject, Guid.NewGuid().ToString()) };
        if (hasClient)
        {
            claims.Add(new Claim(NexaClaims.ClientId, client.ToString()));
        }

        if (isPlatform)
        {
            claims.Add(new Claim(NexaClaims.ActorType, NexaClaims.PlatformActor));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
