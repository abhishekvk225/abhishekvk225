using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Http;
using NexaVerify.Application.Common;
using NexaVerify.Application.Faces;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Faces;
using NexaVerify.Contracts.Identity;

namespace NexaVerify.Api.Controllers.Faces;

/// <summary>
/// Face recognition for the calling client (the client comes from the credential, never from the request). Images are uploaded as
/// multipart form data; the server decides the real file type from the bytes.
/// </summary>
[Route("api/v1/faces")]
public sealed class FacesController : ApiControllerBase
{
    private const int MaxRequestBytes = (6 * 1024 * 1024); // image limit (5 MB) plus form overhead; the processor enforces the exact limit

    private readonly IFaceRecognitionService _recognition;
    private readonly IFaceProfileService _profiles;
    private readonly IRecognitionHistoryService _history;

    public FacesController(IFaceRecognitionService recognition, IFaceProfileService profiles, IRecognitionHistoryService history)
    {
        _recognition = recognition;
        _profiles = profiles;
        _history = history;
    }

    [HttpPost("enroll")]
    [HasPermission(Permissions.Faces.Enroll)]
    [RequestSizeLimit(MaxRequestBytes)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Enroll([FromForm] EnrollFaceRequest request, IFormFile? image, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken) =>
        await WithImage(image, idempotencyKey, async (bytes, key) => ToActionResult(await _recognition.EnrollAsync(request, bytes, key, cancellationToken), r => Ok(r)), cancellationToken);

    [HttpPost("verify")]
    [HasPermission(Permissions.Faces.Verify)]
    [RequestSizeLimit(MaxRequestBytes)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Verify([FromForm] VerifyFaceRequest request, IFormFile? image, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken) =>
        await WithImage(image, idempotencyKey, async (bytes, key) => ToActionResult(await _recognition.VerifyAsync(request, bytes, key, cancellationToken)), cancellationToken);

    [HttpPost("identify")]
    [HasPermission(Permissions.Faces.Identify)]
    [RequestSizeLimit(MaxRequestBytes)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Identify([FromForm] IdentifyFaceRequest request, IFormFile? image, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken) =>
        await WithImage(image, idempotencyKey, async (bytes, key) => ToActionResult(await _recognition.IdentifyAsync(request, bytes, key, cancellationToken)), cancellationToken);

    [HttpPost("detect")]
    [HasPermission(Permissions.Faces.Detect)]
    [RequestSizeLimit(MaxRequestBytes)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Detect(IFormFile? image, CancellationToken cancellationToken) =>
        await WithImage(image, null, async (bytes, _) => ToActionResult(await _recognition.DetectAsync(bytes, cancellationToken)), cancellationToken);

    [HttpGet("profiles")]
    [HasPermission(Permissions.Faces.Read)]
    public async Task<IActionResult> Profiles([FromQuery] FaceProfileListQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _profiles.ListAsync(query, cancellationToken));

    [HttpGet("profiles/{id:guid}")]
    [HasPermission(Permissions.Faces.Read)]
    public async Task<IActionResult> Profile(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _profiles.GetAsync(id, cancellationToken));

    [HttpPut("profiles/{id:guid}")]
    [HasPermission(Permissions.Faces.Manage)]
    public async Task<IActionResult> UpdateProfile(Guid id, UpdateFaceProfileRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _profiles.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("profiles/{id:guid}")]
    [HasPermission(Permissions.Faces.Erase)]
    public async Task<IActionResult> Erase(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _profiles.EraseAsync(id, cancellationToken));

    [HttpDelete("profiles/{id:guid}/templates/{templateId:guid}")]
    [HasPermission(Permissions.Faces.Erase)]
    public async Task<IActionResult> EraseTemplate(Guid id, Guid templateId, CancellationToken cancellationToken) =>
        ToActionResult(await _profiles.DeleteTemplateAsync(id, templateId, cancellationToken));

    [HttpGet("requests")]
    [HasPermission(Permissions.Faces.History)]
    public async Task<IActionResult> Requests([FromQuery] RecognitionHistoryQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _history.ListAsync(query, cancellationToken));

    [HttpGet("requests/{id:guid}")]
    [HasPermission(Permissions.Faces.History)]
    public async Task<IActionResult> RequestDetail(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _history.GetAsync(id, cancellationToken));

    [HttpGet("balance")]
    [HasPermission(Permissions.Faces.Read)]
    public async Task<IActionResult> Balance(CancellationToken cancellationToken) =>
        ToActionResult(await _profiles.GetBalanceAsync(cancellationToken));

    private async Task<IActionResult> WithImage(IFormFile? image, string? idempotencyKey, Func<byte[], string?, Task<IActionResult>> action, CancellationToken cancellationToken)
    {
        if (image is null || image.Length == 0)
        {
            return ProblemFor(new Error(ErrorCodes.ImageInvalid, "An image file is required (form field 'image').", ErrorType.Validation));
        }

        if (image.Length > 5 * 1024 * 1024)
        {
            return ProblemFor(new Error(ErrorCodes.ImageTooLarge, "The image is larger than 5 MB.", ErrorType.PayloadTooLarge));
        }

        if (idempotencyKey is { Length: > 100 })
        {
            return ProblemFor(Error.Validation("Idempotency-Key must be at most 100 characters."));
        }

        await using var stream = image.OpenReadStream();
        using var buffer = new MemoryStream((int)image.Length);
        await stream.CopyToAsync(buffer, cancellationToken);
        return await action(buffer.ToArray(), idempotencyKey);
    }
}
