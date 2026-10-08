using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NexaVerify.Application.Abstractions;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Identity;

namespace NexaVerify.Infrastructure.Identity;

/// <summary>Issues short-lived ES256 access tokens. Permissions are deliberately NOT embedded (resolved server-side from cached roles).</summary>
public sealed class JwtAccessTokenIssuer : IAccessTokenIssuer
{
    private readonly JwtKeyProvider _keys;
    private readonly JwtOptions _options;
    private readonly TimeProvider _time;
    private readonly JsonWebTokenHandler _handler = new();

    public JwtAccessTokenIssuer(JwtKeyProvider keys, IOptions<JwtOptions> options, TimeProvider time)
    {
        _keys = keys;
        _options = options.Value;
        _time = time;
    }

    public AccessToken Issue(User user, IReadOnlyCollection<string> roles, bool mfaEnrolmentRequired = false)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var lifetime = TimeSpan.FromMinutes(_options.AccessTokenMinutes);

        var claims = new Dictionary<string, object>
        {
            [NexaClaims.Subject] = user.Id.ToString(),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
            [JwtRegisteredClaimNames.Email] = user.Email,
            [NexaClaims.Role] = roles.ToArray(),
            [NexaClaims.SecurityVersion] = user.SecurityVersion,
        };

        // The tenant claim and the platform actor are mutually exclusive by construction (HttpCurrentUser enforces it again).
        if (user.IsPlatformUser)
        {
            claims[NexaClaims.ActorType] = NexaClaims.PlatformActor;
        }
        else
        {
            claims[NexaClaims.ClientId] = user.ClientId.ToString();
        }

        if (user.MustChangePassword)
        {
            claims[NexaClaims.MustChangePassword] = "true";
        }

        if (mfaEnrolmentRequired)
        {
            claims[NexaClaims.MfaEnrolmentRequired] = "true";
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(lifetime),
            Claims = claims,
            SigningCredentials = _keys.SigningCredentials,
        };

        return new AccessToken(_handler.CreateToken(descriptor), (int)lifetime.TotalSeconds);
    }
}
