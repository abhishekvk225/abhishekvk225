using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NexaVerify.Api.Http;
using NexaVerify.Application.Common;

namespace NexaVerify.Api.Filters;

/// <summary>
/// Runs the FluentValidation validator (if one is registered) for every action argument before the action executes,
/// returning a uniform VALIDATION_FAILED problem with per-field messages.
/// </summary>
public sealed class ValidationFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var failures = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null)
            {
                continue;
            }

            var validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());
            if (context.HttpContext.RequestServices.GetService(validatorType) is not IValidator validator)
            {
                continue;
            }

            var result = await validator.ValidateAsync(
                new ValidationContext<object>(argument),
                context.HttpContext.RequestAborted);

            foreach (var error in result.Errors)
            {
                if (!failures.TryGetValue(error.PropertyName, out var list))
                {
                    failures[error.PropertyName] = list = [];
                }

                list.Add(error.ErrorMessage);
            }
        }

        if (failures.Count > 0)
        {
            var error = Error.Validation(
                "One or more validation errors occurred.",
                failures.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
            var problem = ApiProblem.Create(context.HttpContext, error);
            context.Result = new ObjectResult(problem)
            {
                StatusCode = problem.Status,
                ContentTypes = { "application/problem+json" },
            };
            return;
        }

        await next();
    }
}
