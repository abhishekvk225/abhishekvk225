using System.ComponentModel.DataAnnotations;
using NexaVerify.Contracts.Identity;

namespace NexaVerify.Web.Services;

public sealed class LoginModel
{
    [Required(ErrorMessage = "Enter your email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter your password.")]
    [StringLength(256)]
    public string Password { get; set; } = string.Empty;
}

public sealed class ForgotPasswordModel
{
    [Required(ErrorMessage = "Enter your email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;
}

public sealed class ResetPasswordModel
{
    public string Email { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    [Required(ErrorMessage = "Choose a new password.")]
    [MinLength(PasswordRules.MinLength, ErrorMessage = "Use at least 12 characters.")]
    [StringLength(128)]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Type the new password again.")]
    [Compare(nameof(NewPassword), ErrorMessage = "The two passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>Mirrors the API password policy (docs/04 §2: minimum length 12, no composition rules). The API stays authoritative.</summary>
public static class PasswordRules
{
    public const int MinLength = 12;
}

public sealed class ChangePasswordModel
{
    [Required(ErrorMessage = "Enter your current password.")]
    [StringLength(256)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Choose a new password.")]
    [MinLength(PasswordRules.MinLength, ErrorMessage = "Use at least 12 characters.")]
    [StringLength(128)]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Type the new password again.")]
    [Compare(nameof(NewPassword), ErrorMessage = "The two passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public sealed class MfaCodeModel
{
    [Required(ErrorMessage = "Enter the 6-digit code from your authenticator app.")]
    [StringLength(8, MinimumLength = 6, ErrorMessage = "Enter the 6-digit code from your authenticator app.")]
    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// Typed client for <c>/api/v1/auth</c> (docs/03 section 2). Login-style calls are anonymous or carry an explicit token;
/// everything else rides on the session through <see cref="IApiGateway"/>.
/// </summary>
public interface IAuthApiClient
{
    Task<ApiResult<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default, string? clientIp = null);

    Task<ApiResult<MeResponse>> GetMeAsync(string accessToken, CancellationToken ct = default, string? clientIp = null);

    Task<ApiResult<bool>> LogoutAsync(string accessToken, string refreshToken, CancellationToken ct = default);

    /// <summary>Always succeeds from the user's point of view (no account enumeration, docs/03 section 2).</summary>
    Task<ApiResult<bool>> ForgotPasswordAsync(ForgotPasswordModel model, CancellationToken ct = default);

    Task<ApiResult<bool>> ResetPasswordAsync(ResetPasswordModel model, CancellationToken ct = default);

    /// <summary>Changes the password for the given session; the API answers with a fresh token pair.</summary>
    Task<ApiResult<LoginResponse>> ChangePasswordAsync(string sessionId, ChangePasswordRequest request, CancellationToken ct = default);

    /// <summary>Second sign-in step: the challenge the API issued after the password, plus an authenticator or recovery code.</summary>
    Task<ApiResult<LoginResponse>> VerifyMfaAsync(VerifyMfaRequest request, CancellationToken ct = default, string? clientIp = null);

    /// <summary>Starts two-factor enrolment for the session's user; the secret is returned once.</summary>
    Task<ApiResult<MfaEnrolmentDto>> BeginMfaEnrolmentAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Confirms enrolment with a code; the API answers with the one-time recovery codes and a fresh session.</summary>
    Task<ApiResult<MfaEnabledDto>> ConfirmMfaEnrolmentAsync(string sessionId, ConfirmMfaRequest request, CancellationToken ct = default);
}
