using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Contracts.Identity;
using NexaVerify.Web.Services;

namespace NexaVerify.Web.Security;

/// <summary>Sign-in, forced password change and sign-out against the API, keeping tokens in the server-side session store.</summary>
public interface IPortalAuth
{
    /// <summary>Logs in with the API and creates a new server-side session (new random id every time: no session fixation).</summary>
    Task<ApiResult<PortalSession>> SignInAsync(string email, string password, CancellationToken ct = default, string? clientIp = null);

    /// <summary>Changes the password; the API revokes other sessions and returns new tokens, which replace the stored ones.</summary>
    Task<ApiResult<bool>> ChangePasswordAsync(string sessionId, ChangePasswordModel model, CancellationToken ct = default);

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
    public async Task<ApiResult<PortalSession>> SignInAsync(string email, string password, CancellationToken ct = default, string? clientIp = null)
    {
        var login = await api.LoginAsync(new LoginRequest(email.Trim(), password), ct, clientIp);
        if (!login.IsSuccess)
        {
            return ApiResult<PortalSession>.Fail(login.Error!);
        }

        var tokens = login.Value;
        var me = await api.GetMeAsync(tokens.AccessToken, ct);
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
        }, CancellationToken.None);

        return updated is null
            ? ApiResult<bool>.Fail(ApiError.Unexpected())
            : ApiResult<bool>.Ok(true);
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
