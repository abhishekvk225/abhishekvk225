using Microsoft.AspNetCore.Mvc;
using NexaVerify.Application.Common;

namespace NexaVerify.Api.Http;

/// <summary>Base for thin controllers: maps <see cref="Result"/> to HTTP in one consistent way.</summary>
[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    protected IActionResult ToActionResult(Result result, Func<IActionResult>? onSuccess = null) =>
        result.IsSuccess ? (onSuccess?.Invoke() ?? NoContent()) : ProblemFor(result.Error!);

    protected IActionResult ToActionResult<T>(Result<T> result, Func<T, IActionResult>? onSuccess = null) =>
        result.IsSuccess ? (onSuccess?.Invoke(result.Value) ?? Ok(result.Value)) : ProblemFor(result.Error!);

    protected IActionResult ProblemFor(Error error) => ApiProblem.ToResult(HttpContext, error);
}
