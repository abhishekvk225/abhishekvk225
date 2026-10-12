using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Filters;
using NexaVerify.Api.Http;
using NexaVerify.Application.Api;
using NexaVerify.Application.Common;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Verification;

namespace NexaVerify.Api.Controllers.Verification;

/// <summary>
/// Submits documents for verification. Processing is asynchronous: the answer is 202 with a request id; poll the status URL or subscribe
/// to the <c>verification.completed</c> webhook. The client always comes from the credential, never from the request. Nothing is charged
/// at accept time. An identical retry with the same <c>Idempotency-Key</c> returns the first request (200, <c>replayed: true</c>).
/// </summary>
[Route("api/v1/verifications")]
public sealed class VerificationSubmissionController : ApiControllerBase
{
    private readonly IBankStatementVerificationService _bankStatements;
    private readonly IPanVerificationService _pan;
    private readonly IPassportVerificationService _passports;
    private readonly VerificationOptions _options;

    public VerificationSubmissionController(
        IBankStatementVerificationService bankStatements, IPanVerificationService pan, IPassportVerificationService passports, IOptions<VerificationOptions> options)
    {
        _bankStatements = bankStatements;
        _pan = pan;
        _passports = passports;
        _options = options.Value;
    }

    /// <summary>Submit a bank statement (PDF or image) as multipart form data with the file in the <c>document</c> part.</summary>
    [HttpPost("bank-statements")]
    [HasPermission(Permissions.Verification.BankStatementSubmit)]
    [Throttle(ThrottlePolicies.VerificationSubmit)]
    [VerificationUploadLimit]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<VerificationAcceptedDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<VerificationAcceptedDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SubmitBankStatement(
        [FromForm] SubmitBankStatementRequest request, IFormFile? document, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken) =>
        await WithDocument(document, cancellationToken, async upload => ToAcceptedResult(await _bankStatements.SubmitAsync(request, upload, idempotencyKey, cancellationToken)));

    /// <summary>Submit a PAN card image or PDF, or just the PAN number (number-only mode) with the details to compare.</summary>
    [HttpPost("pan")]
    [HasPermission(Permissions.Verification.PanSubmit)]
    [Throttle(ThrottlePolicies.VerificationSubmit)]
    [VerificationUploadLimit]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<VerificationAcceptedDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<VerificationAcceptedDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SubmitPan(
        [FromForm] SubmitPanRequest request, IFormFile? document, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken) =>
        await WithDocument(document, cancellationToken, async upload => ToAcceptedResult(await _pan.SubmitAsync(request, upload, idempotencyKey, cancellationToken)));

    /// <summary>Submit a passport image or PDF (the document is required) with the details to compare.</summary>
    [HttpPost("passports")]
    [HasPermission(Permissions.Verification.PassportSubmit)]
    [Throttle(ThrottlePolicies.VerificationSubmit)]
    [VerificationUploadLimit]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<VerificationAcceptedDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<VerificationAcceptedDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SubmitPassport(
        [FromForm] SubmitPassportRequest request, IFormFile? document, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken) =>
        await WithDocument(document, cancellationToken, async upload => ToAcceptedResult(await _passports.SubmitAsync(request, upload, idempotencyKey, cancellationToken)));

    private IActionResult ToAcceptedResult(Result<VerificationAcceptedDto> result)
    {
        if (result.IsFailure)
        {
            return ProblemFor(result.Error!);
        }

        var accepted = result.Value;
        Response.Headers.RetryAfter = accepted.PollAfterSeconds.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
        return accepted.Replayed ? Ok(accepted) : base.Accepted(accepted.StatusUrl, accepted);
    }

    /// <summary>Reads the form file into a bounded buffer (the exact per-file limit is checked BEFORE reading). The file name is never used for storage.</summary>
    private async Task<IActionResult> WithDocument(IFormFile? document, CancellationToken cancellationToken, Func<DocumentUpload?, Task<IActionResult>> action)
    {
        if (document is null || document.Length == 0)
        {
            return await action(null);
        }

        if (document.Length > _options.Upload.MaxFileBytes)
        {
            return ProblemFor(new Error(ErrorCodes.DocumentTooLarge, "The document is larger than the allowed size.", ErrorType.PayloadTooLarge));
        }

        await using var stream = document.OpenReadStream();
        using var buffer = new MemoryStream((int)document.Length);
        await stream.CopyToAsync(buffer, cancellationToken);
        return await action(new DocumentUpload(Path.GetFileName(document.FileName), document.ContentType, buffer.ToArray()));
    }
}
