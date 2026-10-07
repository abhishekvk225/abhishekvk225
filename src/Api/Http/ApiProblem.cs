using Microsoft.AspNetCore.Mvc;
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

    public static ProblemDetails Create(HttpContext http, int status, string code, string detail,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
    {
        var problem = fieldErrors is { Count: > 0 }
            ? new ValidationProblemDetails(fieldErrors.ToDictionary(kv => kv.Key, kv => kv.Value))
            : new ProblemDetails();

        problem.Status = status;
        problem.Title = TitleFor(status);
        problem.Detail = detail;
        problem.Type = $"https://httpstatuses.io/{status}";
        problem.Instance = null; // never echo raw URLs: they may contain identifiers
        problem.Extensions["code"] = code;
        problem.Extensions["correlationId"] = CorrelationIdMiddleware.GetCorrelationId(http);
        return problem;
    }

    public static ProblemDetails Create(HttpContext http, Error error) =>
        Create(http, StatusFor(error.Type), error.Code, error.Message, error.FieldErrors);

    private static string TitleFor(int status) => status switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        402 => "Payment Required",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        413 => "Payload Too Large",
        422 => "Unprocessable Entity",
        429 => "Too Many Requests",
        503 => "Service Unavailable",
        _ => "An error occurred",
    };
}
