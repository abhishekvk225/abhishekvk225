using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using NexaVerify.Api.Middleware;
using NexaVerify.Application.Common;
using NexaVerify.Contracts.Common;

namespace NexaVerify.Api.Http;

/// <summary>Single place that turns application errors into RFC 7807 responses (stable <c>code</c> + <c>correlationId</c>).</summary>
public static class ApiProblem
{
    public static int StatusFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthenticated => StatusCodes.Status401Unauthorized,
        ErrorType.PaymentRequired => StatusCodes.Status402PaymentRequired,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.PayloadTooLarge => StatusCodes.Status413PayloadTooLarge,
        ErrorType.UnprocessableEntity => StatusCodes.Status422UnprocessableEntity,
        ErrorType.TooManyRequests => StatusCodes.Status429TooManyRequests,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>Stable code for framework-generated responses that carry no body of ours (status-code pages).</summary>
    public static string CodeForStatus(int status) => status switch
    {
        400 or 406 or 422 => ErrorCodes.ValidationFailed,
        401 => ErrorCodes.Unauthenticated,
        403 => ErrorCodes.Forbidden,
        404 => ErrorCodes.NotFound,
        405 => ErrorCodes.MethodNotAllowed,
        408 => ErrorCodes.RequestTimeout,
        413 => ErrorCodes.PayloadTooLarge,
        415 => ErrorCodes.UnsupportedMediaType,
        429 => ErrorCodes.RateLimited,
        _ => ErrorCodes.InternalError,
    };

    public static ProblemDetails Create(HttpContext http, int status, string code, string detail,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
    {
        var problem = fieldErrors is { Count: > 0 }
            ? new ValidationProblemDetails(fieldErrors.ToDictionary(kv => FieldName(kv.Key), kv => kv.Value))
            : new ProblemDetails();

        problem.Status = status;
        problem.Title = ReasonPhrases.GetReasonPhrase(status);
        problem.Detail = detail;
        problem.Type = "about:blank";
        problem.Instance = null; // never echo raw URLs: they may contain identifiers
        problem.Extensions["code"] = code;
        problem.Extensions["correlationId"] = CorrelationIdMiddleware.GetCorrelationId(http);
        return problem;
    }

    public static ProblemDetails Create(HttpContext http, Error error) =>
        Create(http, StatusFor(error.Type), error.Code, error.Message, error.FieldErrors);

    public static IActionResult ToResult(HttpContext http, Error error) => ToResult(Create(http, error));

    public static IActionResult ToResult(ProblemDetails problem) => new ObjectResult(problem)
    {
        StatusCode = problem.Status,
        ContentTypes = { "application/problem+json" },
    };

    /// <summary>Field keys follow the JSON convention (camelCase per path segment): <c>Items[0].Name</c> → <c>items[0].name</c>.</summary>
    public static string FieldName(string key) =>
        string.Join('.', key.Split('.').Select(segment =>
            segment.Length == 0 ? segment : char.ToLowerInvariant(segment[0]) + segment[1..]));
}
