using System.ComponentModel.DataAnnotations;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Http;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Common;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Common;

namespace NexaVerify.Api.IntegrationTests.Support;

public sealed record FluentBody(string? Name, int Age);

public sealed class FluentBodyValidator : AbstractValidator<FluentBody>
{
    public FluentBodyValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required.");
        RuleFor(x => x.Age).GreaterThanOrEqualTo(18).WithMessage("Must be an adult.");
    }
}

public sealed class AnnotatedBody
{
    [Required]
    [StringLength(5)]
    public string? Code { get; set; }
}

/// <summary>Endpoints that exist only in tests to exercise the pipeline (error mapping, auth, tenant resolution).</summary>
[Route("test")]
public sealed class TestController : ApiControllerBase
{
    [AllowAnonymous]
    [HttpGet("throw")]
    public IActionResult Throw() => throw new InvalidOperationException("internal detail: SELECT * FROM Users WHERE password='hunter2'");

    [AllowAnonymous]
    [HttpGet("tenant-violation")]
    public IActionResult TenantViolation() => throw new TenantViolationException("tenant B row id 123");

    [AllowAnonymous]
    [HttpGet("domain")]
    public IActionResult Domain() => throw new DomainException(ErrorCodes.LicenseInvalidTransition, "Cannot revoke an expired license.");

    [AllowAnonymous]
    [HttpPost("fluent")]
    public IActionResult Fluent(FluentBody body) => Ok(body);

    [AllowAnonymous]
    [HttpPost("annotated")]
    public IActionResult Annotated(AnnotatedBody body) => Ok(body);

    [AllowAnonymous]
    [HttpGet("result/{kind}")]
    public IActionResult ResultOf(string kind) => ToActionResult(kind switch
    {
        "ok" => Result<string>.Success("fine"),
        "validation" => Error.Validation("bad", new Dictionary<string, string[]> { ["x"] = ["y"] }),
        "unauth" => Error.Unauthenticated(ErrorCodes.ApiKeyInvalid, "bad key"),
        "payment" => Error.PaymentRequired(ErrorCodes.LicenseInsufficientBalance, "no credits"),
        "forbidden" => Error.Forbidden(ErrorCodes.ClientSuspended, "suspended"),
        "notfound" => Error.NotFound(),
        "conflict" => Error.Conflict(ErrorCodes.Conflict, "dup"),
        "toomany" => new Error(ErrorCodes.RateLimited, "slow down", ErrorType.TooManyRequests),
        "unavailable" => new Error(ErrorCodes.FaceProviderUnavailable, "down", ErrorType.Unavailable),
        _ => Error.Failure(ErrorCodes.InternalError, "boom"),
    });

    [Authorize]
    [HttpGet("page")]
    public IActionResult Page([FromQuery] NexaVerify.Contracts.Common.PageRequest query) => Ok(new { query.Page, query.PageSize, query.Search, query.Skip });

    [Authorize]
    [HttpGet("protected")]
    public IActionResult Protected() => Ok(new { ok = true });

    [Authorize]
    [HttpGet("whoami")]
    public IActionResult WhoAmI([FromServices] ITenantContext tenant, [FromServices] ICurrentUser user) =>
        Ok(new { clientId = tenant.ClientId, isPlatform = tenant.IsPlatform, actor = user.ActorType.ToString() });
}
