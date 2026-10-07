using System.ComponentModel.DataAnnotations;

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

public sealed record LoginOutcome(
    Portal Portal,
    string DisplayName,
    string Email,
    string RoleName,
    string? ClientName,
    IReadOnlyList<string> Permissions,
    bool MustChangePassword);

/// <summary>Typed client for <c>/api/v1/auth</c> (docs/03 §2). The BFF implementation replaces the stub.</summary>
public interface IAuthApiClient
{
    Task<ApiResult<LoginOutcome>> LoginAsync(LoginModel model, CancellationToken ct = default);

    /// <summary>Always succeeds from the user's point of view (no account enumeration, docs/03 §2).</summary>
    Task<ApiResult<bool>> ForgotPasswordAsync(ForgotPasswordModel model, CancellationToken ct = default);

    Task<ApiResult<bool>> ResetPasswordAsync(ResetPasswordModel model, CancellationToken ct = default);

    Task LogoutAsync(CancellationToken ct = default);
}

/// <summary>UI-1 stub. "fail@..." fails, an address containing "admin" signs in to the admin console, anything else to the client portal.</summary>
public sealed class StubAuthApiClient : IAuthApiClient
{
    public async Task<ApiResult<LoginOutcome>> LoginAsync(LoginModel model, CancellationToken ct = default)
    {
        await Task.Delay(500, ct);

        if (model.Email.StartsWith("fail", StringComparison.OrdinalIgnoreCase))
        {
            return ApiResult<LoginOutcome>.Fail("INVALID_CREDENTIALS", "The email or password is not correct.", "demo-3f9a1c", 401);
        }

        return model.Email.Contains("admin", StringComparison.OrdinalIgnoreCase)
            ? ApiResult<LoginOutcome>.Ok(new LoginOutcome(Portal.Admin, "Priya Raman", model.Email, "Super Admin", null, WebPermissions.SuperAdminDefaults, false))
            : ApiResult<LoginOutcome>.Ok(new LoginOutcome(Portal.Client, "Alex Morgan", model.Email, "Client Admin", "Acme Corp", WebPermissions.ClientAdminDefaults, false));
    }

    public async Task<ApiResult<bool>> ForgotPasswordAsync(ForgotPasswordModel model, CancellationToken ct = default)
    {
        await Task.Delay(400, ct);
        return ApiResult<bool>.Ok(true);
    }

    public async Task<ApiResult<bool>> ResetPasswordAsync(ResetPasswordModel model, CancellationToken ct = default)
    {
        await Task.Delay(400, ct);
        return string.IsNullOrWhiteSpace(model.Token)
            ? ApiResult<bool>.Fail("TOKEN_INVALID", "This reset link is no longer valid. Request a new one.", "demo-77b2e0", 400)
            : ApiResult<bool>.Ok(true);
    }

    public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
}
