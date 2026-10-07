using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace NexaVerify.Api.Http;

/// <summary>
/// Makes every MVC-generated problem (415, 400 binding errors, client-error mapping) carry the stable <c>code</c> and
/// <c>correlationId</c> like the problems we create ourselves.
/// </summary>
public sealed class NexaProblemDetailsFactory : ProblemDetailsFactory
{
    public override ProblemDetails CreateProblemDetails(
        HttpContext httpContext, int? statusCode = null, string? title = null, string? type = null, string? detail = null, string? instance = null)
    {
        var status = statusCode ?? StatusCodes.Status500InternalServerError;
        return ApiProblem.Create(httpContext, status, ApiProblem.CodeForStatus(status), detail ?? "The request could not be completed.");
    }

    public override ValidationProblemDetails CreateValidationProblemDetails(
        HttpContext httpContext, ModelStateDictionary modelStateDictionary, int? statusCode = null, string? title = null, string? type = null, string? detail = null, string? instance = null)
    {
        var errors = modelStateDictionary
            .Where(kv => kv.Value?.Errors.Count > 0)
            .ToDictionary(
                kv => kv.Key,
                kv => kv.Value!.Errors.Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? "Invalid value." : e.ErrorMessage).ToArray());

        var problem = ApiProblem.Create(httpContext, statusCode ?? StatusCodes.Status400BadRequest, NexaVerify.Contracts.Common.ErrorCodes.ValidationFailed,
            detail ?? "One or more validation errors occurred.", errors);
        return problem as ValidationProblemDetails ?? new ValidationProblemDetails(errors)
        {
            Status = problem.Status,
            Title = problem.Title,
            Detail = problem.Detail,
            Type = problem.Type,
            Extensions = { ["code"] = problem.Extensions["code"], ["correlationId"] = problem.Extensions["correlationId"] },
        };
    }
}
