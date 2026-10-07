using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Common;
using NexaVerify.Infrastructure.Identity;

namespace NexaVerify.Api.Authorization;

/// <summary>Configures bearer validation: pinned algorithm, issuer/audience/lifetime, rotation-aware keys, and a per-request session check.</summary>
public sealed class JwtBearerSetup : IConfigureNamedOptions<JwtBearerOptions>
{
    private readonly JwtKeyProvider _keys;
    private readonly JwtOptions _jwt;

    public JwtBearerSetup(JwtKeyProvider keys, IOptions<JwtOptions> jwt)
    {
        _keys = keys;
        _jwt = jwt.Value;
    }

    public void Configure(JwtBearerOptions options) => Configure(JwtBearerDefaults.AuthenticationScheme, options);

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme)
        {
            return;
        }

        options.MapInboundClaims = false; // keep "sub", "role", "cid"… exactly as issued
        options.RequireHttpsMetadata = true;
        options.SaveToken = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = _jwt.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = _keys.ValidationKeys,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256], // never trust the token's own alg claim
            ClockSkew = TimeSpan.FromSeconds(_jwt.ClockSkewSeconds),
            NameClaimType = NexaClaims.Subject,
            RoleClaimType = NexaClaims.Role,
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var principal = context.Principal;
                var subject = principal?.FindFirst(NexaClaims.Subject)?.Value;
                var version = principal?.FindFirst(NexaClaims.SecurityVersion)?.Value;
                if (!Guid.TryParse(subject, out var userId) || !int.TryParse(version, out var securityVersion))
                {
                    context.Fail("Malformed token.");
                    return;
                }

                var sessions = context.HttpContext.RequestServices.GetRequiredService<ISessionValidator>();
                if (!await sessions.IsValidAsync(userId, securityVersion, context.HttpContext.RequestAborted))
                {
                    context.Fail("Session is no longer valid.");
                }
            },
        };
    }
}
