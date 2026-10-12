using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Processing;

namespace NexaVerify.Api.Filters;

/// <summary>
/// Raises the request body limit of ONE upload route to <c>Verification:Upload:MaxFileBytes + RequestOverheadBytes</c> (the global limit is
/// small and stays in force everywhere else). The value is configuration, not a literal. A body above it is refused by the server before
/// it is buffered (413 PAYLOAD_TOO_LARGE); the exact per-file limit is enforced again by the service (DOCUMENT_TOO_LARGE).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class VerificationUploadLimitAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var upload = context.HttpContext.RequestServices.GetRequiredService<IOptions<VerificationOptions>>().Value.Upload;
        if (context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = upload.MaxFileBytes + upload.RequestOverheadBytes;
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
