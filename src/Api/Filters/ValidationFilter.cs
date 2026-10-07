using System.Collections;
using System.Collections.Concurrent;
using FluentValidation;
using Microsoft.AspNetCore.Mvc.Filters;
using NexaVerify.Api.Http;
using NexaVerify.Application.Common;

namespace NexaVerify.Api.Filters;

/// <summary>
/// Runs the FluentValidation validator (if one is registered) for every action argument — and for each element of
/// collection arguments — before the action executes, returning a uniform VALIDATION_FAILED problem with per-field messages.
/// FluentValidation is the single validation system for request DTOs; every *Request type must have a validator
/// (enforced by an architecture test).
/// </summary>
public sealed class ValidationFilter : IAsyncActionFilter
{
    private const int MaxCollectionElements = 1000;

    private static readonly ConcurrentDictionary<Type, Type> ValidatorTypes = new();

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var failures = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null)
            {
                continue;
            }

            if (argument is IEnumerable enumerable and not string)
            {
                var index = 0;
                foreach (var element in enumerable)
                {
                    if (index >= MaxCollectionElements)
                    {
                        Add(failures, "$", $"At most {MaxCollectionElements} items are accepted.");
                        break;
                    }

                    if (element is not null)
                    {
                        await ValidateAsync(context, element, $"[{index}].", failures);
                    }

                    index++;
                }
            }
            else
            {
                await ValidateAsync(context, argument, string.Empty, failures);
            }
        }

        if (failures.Count > 0)
        {
            var error = Error.Validation(
                "One or more validation errors occurred.",
                failures.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
            context.Result = ApiProblem.ToResult(context.HttpContext, error);
            return;
        }

        await next();
    }

    private static async Task ValidateAsync(ActionExecutingContext context, object instance, string prefix, Dictionary<string, List<string>> failures)
    {
        var validatorType = ValidatorTypes.GetOrAdd(instance.GetType(), t => typeof(IValidator<>).MakeGenericType(t));
        if (context.HttpContext.RequestServices.GetService(validatorType) is not IValidator validator)
        {
            return;
        }

        var result = await validator.ValidateAsync(new ValidationContext<object>(instance), context.HttpContext.RequestAborted);
        foreach (var error in result.Errors)
        {
            Add(failures, prefix + error.PropertyName, error.ErrorMessage);
        }
    }

    private static void Add(Dictionary<string, List<string>> failures, string key, string message)
    {
        if (!failures.TryGetValue(key, out var list))
        {
            failures[key] = list = [];
        }

        list.Add(message);
    }
}
