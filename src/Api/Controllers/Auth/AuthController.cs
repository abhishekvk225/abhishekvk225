using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexaVerify.Api.Configuration;
using NexaVerify.Api.Http;
using NexaVerify.Application.Identity;
using NexaVerify.Contracts.Identity;

namespace NexaVerify.Api.Controllers.Auth;

/// <summary>Authentication and session endpoints. Thin: each action calls exactly one application method.</summary>
[Route("api/v1/auth")]
public sealed class AuthController : ApiControllerBase
{
    private readonly IAuthService _auth;
    private readonly IMfaService _mfa;

    public AuthController(IAuthService auth, IMfaService mfa)
    {
        _auth = auth;
        _mfa = mfa;
    }

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitSettings.AuthPolicy)]
    [HttpPost("login")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _auth.LoginAsync(request, cancellationToken));

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitSettings.AuthPolicy)]
    [HttpPost("refresh")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _auth.RefreshAsync(request, cancellationToken));

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _auth.LogoutAsync(request, cancellationToken));

    /// <summary>Always answers 202 so callers cannot learn which emails have accounts.</summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitSettings.AuthPolicy)]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _auth.ForgotPasswordAsync(request, cancellationToken), Accepted);

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitSettings.AuthPolicy)]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _auth.ResetPasswordAsync(request, cancellationToken));

    // [Authorize] only (no permission): reachable while a forced password change is pending.
    [Authorize]
    [EnableRateLimiting(RateLimitSettings.AuthPolicy)]
    [HttpPost("change-password")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _auth.ChangePasswordAsync(request, cancellationToken));

    [Authorize]
    [HttpGet("me")]
    [ProducesResponseType<MeResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Me(CancellationToken cancellationToken) =>
        ToActionResult(await _auth.GetMeAsync(cancellationToken));

    /// <summary>Second sign-in step: the challenge returned by <c>login</c> plus an authenticator or recovery code.</summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitSettings.AuthPolicy)]
    [HttpPost("mfa/verify")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> VerifyMfa(VerifyMfaRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _mfa.VerifyChallengeAsync(request, cancellationToken));

    // The MFA self-service endpoints are [Authorize]-only on purpose: a token flagged "mfa enrolment required" must reach them.
    [Authorize]
    [HttpGet("mfa")]
    [ProducesResponseType<MfaStatusDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MfaStatus(CancellationToken cancellationToken) =>
        ToActionResult(await _mfa.GetStatusAsync(cancellationToken));

    /// <summary>Starts (or restarts) enrolment. The shared secret is returned once, as base32 and as an otpauth URI.</summary>
    [Authorize]
    [EnableRateLimiting(RateLimitSettings.AuthPolicy)]
    [HttpPost("mfa/enroll")]
    [ProducesResponseType<MfaEnrolmentDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> BeginMfaEnrolment(CancellationToken cancellationToken) =>
        ToActionResult(await _mfa.BeginEnrolmentAsync(cancellationToken));

    /// <summary>Proves the authenticator works; turns MFA on, returns the one-time recovery codes and a fresh session.</summary>
    [Authorize]
    [EnableRateLimiting(RateLimitSettings.AuthPolicy)]
    [HttpPost("mfa/enroll/confirm")]
    [ProducesResponseType<MfaEnabledDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ConfirmMfaEnrolment(ConfirmMfaRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _mfa.ConfirmEnrolmentAsync(request, cancellationToken));

    [Authorize]
    [EnableRateLimiting(RateLimitSettings.AuthPolicy)]
    [HttpPost("mfa/recovery-codes")]
    [ProducesResponseType<RecoveryCodesDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RegenerateRecoveryCodes(RegenerateRecoveryCodesRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _mfa.RegenerateRecoveryCodesAsync(request, cancellationToken));
}
