using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using NexaVerify.Api.Http;
using NexaVerify.Application.Api;
using NexaVerify.Application.Common;
using NexaVerify.Contracts.Common;

namespace NexaVerify.Api.Authorization;

/// <summary>Authenticates the <c>X-Api-Key</c> header. The key is never logged; failures are reported with stable error codes.</summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string PolicyScheme = "NexaAuth";
    private const string ErrorItem = "nexa.auth.error";

    public ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HttpHeaderNames.ApiKey, out var values))
        {
            return AuthenticateResult.NoResult();
        }

        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            return Fail(Error.Unauthenticated(ErrorCodes.ApiKeyInvalid, "The API key is not valid."));
        }

        var ip = Context.Connection.RemoteIpAddress is { } address ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString() : null;
        var result = await Context.RequestServices.GetRequiredService<IApiKeyAuthenticator>().AuthenticateAsync(values[0]!.Trim(), ip, Context.RequestAborted);
        if (result.IsFailure)
        {
            return Fail(result.Error!);
        }

        var identity = result.Value;
        var claims = new List<Claim>
        {
            new(NexaClaims.Subject, identity.KeyId.ToString()),
            new(NexaClaims.ClientId, identity.ClientId.ToString()),
            new(NexaClaims.ActorType, NexaClaims.ApiKeyActor),
        };
        claims.AddRange(identity.Scopes.Select(s => new Claim(NexaClaims.Scope, s)));
        if (identity.RateLimitPerMinute is { } limit)
        {
            claims.Add(new Claim(NexaClaims.RateLimitPerMinute, limit.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, NexaClaims.Subject, NexaClaims.Role));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var error = Context.Items[ErrorItem] as Error ?? Error.Unauthenticated(ErrorCodes.Unauthenticated, "Authentication is required.");
        var status = error.Type == ErrorType.Forbidden ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
        Response.StatusCode = status;
        Response.ContentType = "application/problem+json";
        await Response.WriteAsJsonAsync(ApiProblem.Create(Context, status, error.Code, error.Message), options: null, contentType: "application/problem+json");
    }

    private AuthenticateResult Fail(Error error)
    {
        Context.Items[ErrorItem] = error;
        return AuthenticateResult.Fail(error.Code);
    }
}
