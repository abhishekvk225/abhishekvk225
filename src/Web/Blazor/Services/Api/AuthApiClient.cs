using NexaVerify.Contracts.Identity;

namespace NexaVerify.Web.Services;

/// <summary>Real <see cref="IAuthApiClient"/> over <see cref="IApiGateway"/>. Passwords and tokens are never logged.</summary>
public sealed class AuthApiClient(IApiGateway api) : IAuthApiClient
{
    public Task<ApiResult<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default, string? clientIp = null) =>
        api.SendAsync<LoginResponse>(HttpMethod.Post, "auth/login", request, ct, ApiCallOptions.None with { ClientIp = clientIp });

    public Task<ApiResult<MeResponse>> GetMeAsync(string accessToken, CancellationToken ct = default, string? clientIp = null) =>
        api.GetAsync<MeResponse>("auth/me", ct, new ApiCallOptions { BearerToken = accessToken, ClientIp = clientIp });

    public Task<ApiResult<bool>> LogoutAsync(string accessToken, string refreshToken, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, "auth/logout", new LogoutRequest(refreshToken), ct, new ApiCallOptions { BearerToken = accessToken });

    public Task<ApiResult<bool>> ForgotPasswordAsync(ForgotPasswordModel model, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, "auth/forgot-password", new ForgotPasswordRequest(model.Email.Trim()), ct, ApiCallOptions.None);

    public Task<ApiResult<bool>> ResetPasswordAsync(ResetPasswordModel model, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, "auth/reset-password", new ResetPasswordRequest(model.Email.Trim(), model.Token, model.NewPassword), ct, ApiCallOptions.None);

    public Task<ApiResult<LoginResponse>> ChangePasswordAsync(string sessionId, ChangePasswordRequest request, CancellationToken ct = default) =>
        api.SendAsync<LoginResponse>(HttpMethod.Post, "auth/change-password", request, ct, new ApiCallOptions { SessionId = sessionId });

    public Task<ApiResult<LoginResponse>> VerifyMfaAsync(VerifyMfaRequest request, CancellationToken ct = default, string? clientIp = null) =>
        api.SendAsync<LoginResponse>(HttpMethod.Post, "auth/mfa/verify", request, ct, ApiCallOptions.None with { ClientIp = clientIp });

    public Task<ApiResult<MfaEnrolmentDto>> BeginMfaEnrolmentAsync(string sessionId, CancellationToken ct = default) =>
        api.SendAsync<MfaEnrolmentDto>(HttpMethod.Post, "auth/mfa/enroll", null, ct, new ApiCallOptions { SessionId = sessionId });

    public Task<ApiResult<MfaEnabledDto>> ConfirmMfaEnrolmentAsync(string sessionId, ConfirmMfaRequest request, CancellationToken ct = default) =>
        api.SendAsync<MfaEnabledDto>(HttpMethod.Post, "auth/mfa/enroll/confirm", request, ct, new ApiCallOptions { SessionId = sessionId });
}
