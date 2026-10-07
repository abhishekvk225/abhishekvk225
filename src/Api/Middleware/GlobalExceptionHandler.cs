using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Http;
using NexaVerify.Application.Common;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Common;

namespace NexaVerify.Api.Middleware;

/// <summary>
/// Last-resort handler: converts any unhandled exception into a ProblemDetails response without leaking
/// messages, stack traces, SQL or identifiers. The full exception goes to the (redacted) log with the correlation id.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    /// <summary>Non-standard "client closed request" status; nothing is written, it only makes access logs accurate.</summary>
    private const int ClientClosedRequest = 499;

    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = ClientClosedRequest;
            return true; // client went away; nothing to write
        }

        ProblemDetails problem;
        switch (exception)
        {
            case TenantViolationException:
                // Never reveal that another tenant's data exists.
                _logger.LogError(exception, "Tenant isolation violation blocked");
                problem = ApiProblem.Create(httpContext, StatusCodes.Status404NotFound, ErrorCodes.NotFound,
                    "The requested resource was not found.");
                break;

            case ConcurrencyConflictException:
                problem = ApiProblem.Create(httpContext, StatusCodes.Status409Conflict, ErrorCodes.ConcurrencyConflict, exception.Message);
                break;

            case UniqueConstraintViolationException:
                problem = ApiProblem.Create(httpContext, StatusCodes.Status409Conflict, ErrorCodes.Conflict, exception.Message);
                break;

            case DomainException domain:
                // Domain messages are authored for clients and must not embed identifiers or other tenants' values.
                _logger.LogWarning(exception, "Domain rule violated: {Code}", domain.Code);
                problem = ApiProblem.Create(httpContext, StatusCodes.Status409Conflict, domain.Code, domain.Message);
                break;

            case BadHttpRequestException bad:
                var status = bad.StatusCode is >= 400 and < 500 ? bad.StatusCode : StatusCodes.Status400BadRequest;
                problem = ApiProblem.Create(httpContext, status, ApiProblem.CodeForStatus(status),
                    status == StatusCodes.Status413PayloadTooLarge ? "The request body is too large." : "The request could not be understood.");
                break;

            case TimeoutException or OperationCanceledException:
                _logger.LogWarning(exception, "Request timed out");
                problem = ApiProblem.Create(httpContext, StatusCodes.Status503ServiceUnavailable, ErrorCodes.RequestTimeout,
                    "The request took too long to complete. Please retry.");
                break;

            default:
                _logger.LogError(exception, "Unhandled exception");
                problem = ApiProblem.Create(httpContext, StatusCodes.Status500InternalServerError, ErrorCodes.InternalError,
                    "An unexpected error occurred. Quote the correlation id when contacting support.");
                break;
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/problem+json";
        await httpContext.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken);
        return true;
    }
}
