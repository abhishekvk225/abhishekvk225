using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Identity;
using NexaVerify.Domain.Identity;

namespace NexaVerify.Application.Identity;

/// <summary>Creates the access token + rotating refresh token pair. The single place that turns a proven identity into a session.</summary>
public interface ISessionIssuer
{
    Task<LoginResponse> IssueAsync(User user, Guid? familyId, DateTime now, CancellationToken cancellationToken, RefreshToken? replacing = null);
}

public sealed class SessionIssuer : ISessionIssuer
{
    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly ISecureTokenService _tokens;
    private readonly IAccessTokenIssuer _accessTokens;
    private readonly IMfaPolicy _mfa;
    private readonly IRequestInfo _request;
    private readonly AuthOptions _options;

    public SessionIssuer(
        IUserRepository users,
        IRefreshTokenRepository refreshTokens,
        ISecureTokenService tokens,
        IAccessTokenIssuer accessTokens,
        IMfaPolicy mfa,
        IRequestInfo request,
        IOptions<AuthOptions> options)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _tokens = tokens;
        _accessTokens = accessTokens;
        _mfa = mfa;
        _request = request;
        _options = options.Value;
    }

    public async Task<LoginResponse> IssueAsync(User user, Guid? familyId, DateTime now, CancellationToken cancellationToken, RefreshToken? replacing = null)
    {
        var roles = await _users.GetRoleNamesAsync(user.Id, cancellationToken);

        // A required-but-missing second factor yields a token that only opens the enrolment endpoints (like a forced password change).
        var enrolmentRequired = await _mfa.EnrolmentRequiredAsync(user, roles, cancellationToken);
        var access = _accessTokens.Issue(user, roles, enrolmentRequired);

        var absolute = replacing?.AbsoluteExpiresAt ?? now.AddDays(_options.RefreshTokenAbsoluteDays);
        var raw = _tokens.CreateToken();
        var refresh = RefreshToken.Issue(
            user,
            _tokens.Hash(raw),
            familyId ?? Guid.CreateVersion7(),
            now,
            TimeSpan.FromDays(_options.RefreshTokenSlidingDays),
            absolute,
            _request.IpAddress,
            _request.UserAgent);
        _refreshTokens.Add(refresh);
        replacing?.LinkSuccessor(refresh.Id);

        return new LoginResponse(access.Value, "Bearer", access.ExpiresInSeconds, raw, user.MustChangePassword, user.ToSummary(roles), enrolmentRequired);
    }
}
