using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Contracts.Identity;
using NexaVerify.Web.Services;

namespace NexaVerify.Web.Security;

/// <summary>
/// The outcome of the password step: either a finished session, or a pending second factor (the API challenge is returned for the
/// caller to park server-side; it must never be sent to the browser).
/// </summary>
public sealed record SignInStep(PortalSession? Session, MfaPending? Pending);

/// <summary>Sign-in (one or two steps), forced password change, two-factor enrolment and sign-out against the API, keeping tokens in the server-side session store.</summary>
public interface IPortalAuth
{
    /// <summary>Password step. Creates a server-side session (new random id every time: no session fixation) unless the account needs a second factor.</summary>
    Task<ApiResult<SignInStep>> BeginSignInAsync(string email, string password, CancellationToken ct = default, string? clientIp = null);

    /// <summary>For callers that cannot ask for a second factor: signs in, and fails (without creating a session) when one is required.</summary>
    Task<ApiResult<PortalSession>> SignInAsync(string email, string password, CancellationToken ct = default, string? clientIp = null);

    /// <summary>Second step: proves the code against the parked challenge and creates the session.</summary>
    Task<ApiResult<PortalSession>> CompleteMfaSignInAsync(MfaPending pending, string code, CancellationToken ct = default);

    /// <summary>Changes the password; the API revokes other sessions and returns new tokens, which replace the stored ones.</summary>
    Task<ApiResult<bool>> ChangePasswordAsync(string sessionId, ChangePasswordModel model, CancellationToken ct = default);

    Task<ApiResult<MfaEnrolmentDto>> BeginMfaEnrolmentAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Confirms enrolment; the session takes over the fresh tokens (turning MFA on ended the old ones). Returns the one-time recovery codes.</summary>
    Task<ApiResult<IReadOnlyList<string>>> ConfirmMfaEnrolmentAsync(string sessionId, string code, CancellationToken ct = default);

    /// <summary>Revokes the refresh-token family at the API (best effort) and destroys the session.</summary>
    Task SignOutAsync(string sessionId, CancellationToken ct = default);
}

public sealed class PortalAuth(
    IAuthApiClient api,
    ISessionStore store,
    TokenRefreshCoordinator refresher,
    TimeProvider clock,
    IOptions<SessionOptions> options,
    ILogger<PortalAuth> logger) : IPortalAuth
{
    public const string MfaRequiredCode = "MFA_REQUIRED";

    public async Task<ApiResult<SignInStep>> BeginSignInAsync(string email, string password, CancellationToken ct = default, string? clientIp = null)
    {
        var trimmed = email.Trim();
        var login = await api.LoginAsync(new LoginRequest(trimmed, password), ct, clientIp);
        if (!login.IsSuccess)
        {
            return ApiResult<SignInStep>.Fail(login.Error!);
        }

        var tokens = login.Value;
        if (tokens.MfaRequired)
        {
            if (string.IsNullOrEmpty(tokens.MfaChallengeToken))
            {
                return ApiResult<SignInStep>.Fail(ApiError.Unexpected());
            }

            var lifetime = TimeSpan.FromSeconds(Math.Clamp(tokens.MfaChallengeExpiresIn, 30, 1800));
            return ApiResult<SignInStep>.Ok(new SignInStep(null, new MfaPending(tokens.MfaChallengeToken, trimmed, clientIp, null, clock.GetUtcNow() + lifetime)));
        }

        var session = await CreateSessionAsync(tokens, clientIp, ct);
        return session.IsSuccess ? ApiResult<SignInStep>.Ok(new SignInStep(session.Value, null)) : ApiResult<SignInStep>.Fail(session.Error!);
    }

    public async Task<ApiResult<PortalSession>> SignInAsync(string email, string password, CancellationToken ct = default, string? clientIp = null)
    {
        var step = await BeginSignInAsync(email, password, ct, clientIp);
        if (!step.IsSuccess)
        {
            return ApiResult<PortalSession>.Fail(step.Error!);
        }

        return step.Value.Session is { } session
            ? ApiResult<PortalSession>.Ok(session)
            : ApiResult<PortalSession>.Fail(MfaRequiredCode, "A second sign-in step is required.", null, 401);
    }

    public async Task<ApiResult<PortalSession>> CompleteMfaSignInAsync(MfaPending pending, string code, CancellationToken ct = default)
    {
        var verified = await api.VerifyMfaAsync(new VerifyMfaRequest(pending.ChallengeToken, code.Trim()), ct, pending.ClientIp);
        return verified.IsSuccess
            ? await CreateSessionAsync(verified.Value, pending.ClientIp, ct)
            : ApiResult<PortalSession>.Fail(verified.Error!);
    }

    private async Task<ApiResult<PortalSession>> CreateSessionAsync(LoginResponse tokens, string? clientIp, CancellationToken ct)
    {
        var me = await api.GetMeAsync(tokens.AccessToken, ct, clientIp);
        if (!me.IsSuccess)
        {
            // Without a profile we cannot build a session; do not leave a live refresh token behind.
            await api.LogoutAsync(tokens.AccessToken, tokens.RefreshToken, CancellationToken.None);
            return ApiResult<PortalSession>.Fail(me.Error!);
        }

        var now = clock.GetUtcNow();
        var profile = me.Value;
        var session = new PortalSession
        {
            Id = DistributedSessionStore.NewId(),
            AccessToken = tokens.AccessToken,
            AccessTokenExpiresAt = now.AddSeconds(tokens.ExpiresIn),
            RefreshToken = tokens.RefreshToken,
            UserId = profile.User.Id,
            Email = profile.User.Email,
            FullName = profile.User.FullName,
            Portal = profile.User.IsPlatformUser ? PortalKinds.Admin : PortalKinds.Client,
            Roles = profile.User.Roles,
            ClientId = profile.User.ClientId,
            ClientName = profile.Client?.Name,
            Permissions = profile.Permissions,
            MustChangePassword = tokens.MustChangePassword || profile.MustChangePassword,
            MfaEnrolmentRequired = tokens.MfaEnrolmentRequired || profile.MfaEnrolmentRequired,
            ClientIp = clientIp,
            CreatedAt = now,
            LastSeenAt = now,
            AbsoluteExpiresAt = now + options.Value.AbsoluteTimeout,
        };
        await store.SaveAsync(session, ct);
        return ApiResult<PortalSession>.Ok(session);
    }

    public async Task<ApiResult<bool>> ChangePasswordAsync(string sessionId, ChangePasswordModel model, CancellationToken ct = default)
    {
        var result = await api.ChangePasswordAsync(sessionId, new ChangePasswordRequest(model.CurrentPassword, model.NewPassword), ct);
        if (!result.IsSuccess)
        {
            return ApiResult<bool>.Fail(result.Error!);
        }

        var tokens = result.Value;
        var updated = await store.UpdateAsync(sessionId, s => s with
        {
            AccessToken = tokens.AccessToken,
            AccessTokenExpiresAt = clock.GetUtcNow().AddSeconds(tokens.ExpiresIn),
            RefreshToken = tokens.RefreshToken,
            MustChangePassword = tokens.MustChangePassword,
            MfaEnrolmentRequired = tokens.MfaEnrolmentRequired,
        }, CancellationToken.None);

        return updated is null
            ? ApiResult<bool>.Fail(ApiError.Unexpected())
            : ApiResult<bool>.Ok(true);
    }

    public Task<ApiResult<MfaEnrolmentDto>> BeginMfaEnrolmentAsync(string sessionId, CancellationToken ct = default) =>
        api.BeginMfaEnrolmentAsync(sessionId, ct);

    public async Task<ApiResult<IReadOnlyList<string>>> ConfirmMfaEnrolmentAsync(string sessionId, string code, CancellationToken ct = default)
    {
        var result = await api.ConfirmMfaEnrolmentAsync(sessionId, new ConfirmMfaRequest(code.Trim()), ct);
        if (!result.IsSuccess)
        {
            return ApiResult<IReadOnlyList<string>>.Fail(result.Error!);
        }

        // Turning MFA on ended every earlier token: the session must take over the new pair or the next call would be refused.
        var tokens = result.Value.Session;
        var updated = await store.UpdateAsync(sessionId, s => s with
        {
            AccessToken = tokens.AccessToken,
            AccessTokenExpiresAt = clock.GetUtcNow().AddSeconds(tokens.ExpiresIn),
            RefreshToken = tokens.RefreshToken,
            MustChangePassword = tokens.MustChangePassword,
            MfaEnrolmentRequired = tokens.MfaEnrolmentRequired,
        }, CancellationToken.None);

        return updated is null
            ? ApiResult<IReadOnlyList<string>>.Fail(ApiError.Unexpected())
            : ApiResult<IReadOnlyList<string>>.Ok(result.Value.RecoveryCodes);
    }

    public async Task SignOutAsync(string sessionId, CancellationToken ct = default)
    {
        var session = await store.GetAsync(sessionId, ct);
        if (session is not null && session.AccessTokenExpiresAt <= clock.GetUtcNow().AddSeconds(5))
        {
            // Logout needs a valid access token; renew it first so the refresh-token family really gets revoked.
            var renewed = await refresher.RefreshAsync(sessionId, session.AccessToken, ct);
            session = renewed.Status == RefreshStatus.Refreshed ? await store.GetAsync(sessionId, ct) : null;
        }

        if (session is not null)
        {
            var result = await api.LogoutAsync(session.AccessToken, session.RefreshToken, ct);
            if (!result.IsSuccess)
            {
                logger.LogInformation("API logout did not complete (status {Status}); the local session is removed anyway.", result.Error!.Status);
            }
        }

        await store.RemoveAsync(sessionId, CancellationToken.None);
    }
}
