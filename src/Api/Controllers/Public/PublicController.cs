using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexaVerify.Api.Configuration;
using NexaVerify.Api.Http;
using NexaVerify.Application.Identity;
using NexaVerify.Application.Public;
using NexaVerify.Contracts.Public;

namespace NexaVerify.Api.Controllers.Public;

/// <summary>
/// Anonymous endpoints behind the public website (called server-to-server by the portal; no CORS). Thin: each action calls exactly one
/// application method. Bodies are tiny, so request size is capped far below the global limit.
/// </summary>
[AllowAnonymous]
[Route("api/v1/public")]
public sealed class PublicController : ApiControllerBase
{
    private const long MaxBodyBytes = 16 * 1024;

    private readonly IPublicInfoService _info;
    private readonly ISignupService _signup;
    private readonly IContactService _contact;

    public PublicController(IPublicInfoService info, ISignupService signup, IContactService contact)
    {
        _info = info;
        _signup = signup;
        _contact = contact;
    }

    /// <summary>Plans flagged public. Cacheable for 60 seconds. No prices: <c>displayPrice</c> is optional text, otherwise the site shows "Contact us".</summary>
    [HttpGet("plans")]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType<IReadOnlyList<PublicPlanDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Plans(CancellationToken cancellationToken) =>
        ToActionResult(await _info.GetPlansAsync(cancellationToken));

    [HttpGet("config")]
    [ProducesResponseType<PublicConfigDto>(StatusCodes.Status200OK)]
    public IActionResult Config() => Ok(_info.GetConfig());

    /// <summary>Always answers 202 with an empty body, whether the address is new, pending or already registered.</summary>
    [HttpPost("signup")]
    [RequestSizeLimit(MaxBodyBytes)]
    [EnableRateLimiting(RateLimitSettings.AuthPolicy)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Signup(SignupRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _signup.SignupAsync(request, cancellationToken), () => Accepted(new { }));

    /// <summary>Re-sends the verification email (fresh token, at most 3 times per sign-up). Always 202, whatever the address.</summary>
    [HttpPost("signup/resend")]
    [RequestSizeLimit(MaxBodyBytes)]
    [EnableRateLimiting(RateLimitSettings.AuthPolicy)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Resend(ResendSignupRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _signup.ResendAsync(request, cancellationToken), () => Accepted(new { }));

    /// <summary>Completes a sign-up from the emailed link. 204 on success; any failure is the same generic 400.</summary>
    [HttpPost("signup/verify")]
    [RequestSizeLimit(MaxBodyBytes)]
    [EnableRateLimiting(RateLimitSettings.AuthPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Verify(VerifySignupRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _signup.VerifyAsync(request, cancellationToken));

    [HttpPost("contact")]
    [RequestSizeLimit(MaxBodyBytes)]
    [EnableRateLimiting(RateLimitSettings.AuthPolicy)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Contact(SubmitContactRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _contact.SubmitAsync(request, cancellationToken), () => Accepted(new { }));
}
