using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Http;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Common;
using NexaVerify.Infrastructure.Tenancy;

namespace NexaVerify.Api.Middleware;

/// <summary>
/// Last-resort handler: converts any unhandled exception into a ProblemDetails response without leaking
/// messages, stack traces, SQL or identifiers. The full exception goes to the (redacted) log with the correlation id.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
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

            case DomainException domain:
                _logger.LogWarning(exception, "Domain rule violated: {Code}", domain.Code);
                problem = ApiProblem.Create(httpContext, StatusCodes.Status409Conflict, domain.Code, domain.Message);
                break;

            case BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge }:
                problem = ApiProblem.Create(httpContext, StatusCodes.Status413PayloadTooLarge, ErrorCodes.PayloadTooLarge,
                    "The request body is too large.");
                break;

            case BadHttpRequestException bad:
                problem = ApiProblem.Create(httpContext, bad.StatusCode, ErrorCodes.ValidationFailed,
                    "The request could not be understood.");
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
