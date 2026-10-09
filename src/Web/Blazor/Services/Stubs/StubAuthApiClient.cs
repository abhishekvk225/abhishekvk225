using NexaVerify.Contracts.Identity;

namespace NexaVerify.Web.Services;

/// <summary>
/// Optional design-review stub (<c>Ui:UseStubClients</c>, Development/UiDemo only). It accepts any password, so the app refuses to
/// enable it anywhere else. "fail@..." fails, an address containing "admin" signs in to the admin console, anything else to the client portal.
/// The tokens it hands out are placeholders that the (real) API would reject.
/// </summary>
public sealed class StubAuthApiClient : IAuthApiClient
{
    public async Task<ApiResult<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default, string? clientIp = null)
    {
        await Task.Delay(300, ct);
        if (request.Email.StartsWith("fail", StringComparison.OrdinalIgnoreCase))
        {
            return ApiResult<LoginResponse>.Fail("UNAUTHENTICATED", "The email or password is not correct.", "demo-3f9a1c", 401);
        }

        var admin = request.Email.Contains("admin", StringComparison.OrdinalIgnoreCase);
        var user = new UserSummary(
            Guid.NewGuid(), request.Email, admin ? "Priya Raman" : "Alex Morgan", admin, admin ? null : Guid.NewGuid(),
            [admin ? SystemRoles.SuperAdmin : SystemRoles.ClientAdmin]);
        return ApiResult<LoginResponse>.Ok(new LoginResponse(admin ? "stub-admin" : "stub-client", "Bearer", 900, "stub-refresh", false, user));
    }

    public Task<ApiResult<MeResponse>> GetMeAsync(string accessToken, CancellationToken ct = default, string? clientIp = null)
    {
        var admin = accessToken == "stub-admin";
        var role = admin ? SystemRoles.SuperAdmin : SystemRoles.ClientAdmin;
        var user = new UserSummary(
            Guid.NewGuid(), admin ? "priya@nexaverify.example" : "alex@acme.example", admin ? "Priya Raman" : "Alex Morgan", admin,
            admin ? null : Guid.NewGuid(), [role]);
        var client = admin ? null : new NexaVerify.Contracts.Tenancy.ClientSummary(user.ClientId!.Value, "ACME", "Acme Corp", "Active", "UTC");
        string[] billing = admin
            ? [BillingPermissions.PacksManage, BillingPermissions.OrdersRead, BillingPermissions.Refund]
            : [BillingPermissions.Read, BillingPermissions.Manage];
        return Task.FromResult(ApiResult<MeResponse>.Ok(new MeResponse(user, SystemRoles.PermissionsFor(role).Concat(billing).Distinct().ToList(), false, client)));
    }

    public Task<ApiResult<bool>> LogoutAsync(string accessToken, string refreshToken, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<bool>.Ok(true));

    public async Task<ApiResult<bool>> ForgotPasswordAsync(ForgotPasswordModel model, CancellationToken ct = default)
    {
        await Task.Delay(200, ct);
        return ApiResult<bool>.Ok(true);
    }

    public async Task<ApiResult<bool>> ResetPasswordAsync(ResetPasswordModel model, CancellationToken ct = default)
    {
        await Task.Delay(200, ct);
        return string.IsNullOrWhiteSpace(model.Token)
            ? ApiResult<bool>.Fail("TOKEN_INVALID", "This reset link is no longer valid. Request a new one.", "demo-77b2e0", 400)
            : ApiResult<bool>.Ok(true);
    }

    public Task<ApiResult<LoginResponse>> ChangePasswordAsync(string sessionId, ChangePasswordRequest request, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<LoginResponse>.Fail("NOT_AVAILABLE", "Not available in the preview build.", null, 400));

    public Task<ApiResult<LoginResponse>> VerifyMfaAsync(VerifyMfaRequest request, CancellationToken ct = default, string? clientIp = null) =>
        Task.FromResult(ApiResult<LoginResponse>.Fail("NOT_AVAILABLE", "Not available in the preview build.", null, 400));

    public Task<ApiResult<MfaEnrolmentDto>> BeginMfaEnrolmentAsync(string sessionId, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<MfaEnrolmentDto>.Fail("NOT_AVAILABLE", "Not available in the preview build.", null, 400));

    public Task<ApiResult<MfaEnabledDto>> ConfirmMfaEnrolmentAsync(string sessionId, ConfirmMfaRequest request, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<MfaEnabledDto>.Fail("NOT_AVAILABLE", "Not available in the preview build.", null, 400));
}
