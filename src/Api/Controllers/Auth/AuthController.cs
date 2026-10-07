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

    public AuthController(IAuthService auth)
    {
        _auth = auth;
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
}
