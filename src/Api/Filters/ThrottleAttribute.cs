using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NexaVerify.Api.Http;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Api;

namespace NexaVerify.Api.Filters;

/// <summary>
/// Limits how often one signed-in principal (user or API key) may call an expensive or abusable action. Runs after authorisation, so
/// anonymous callers never reach the counters; the limit is shared across API nodes and configured under <c>Throttle:*</c>.
/// Answers 429 with <c>Retry-After</c> and the standard problem body.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ThrottleAttribute : Attribute, IAsyncActionFilter
{
    public ThrottleAttribute(string policy)
    {
        Policy = policy;
    }

    public string Policy { get; }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.RequestServices.GetRequiredService<ICurrentUser>();
        if (user is not { IsAuthenticated: true } || (user.ActorId ?? user.ClientId) is not { } principal)
        {
            await next();
            return;
        }

        var throttle = context.HttpContext.RequestServices.GetRequiredService<IPrincipalThrottle>();
        var (error, retryAfter) = await throttle.TryAcquireAsync(Policy, principal, context.HttpContext.RequestAborted);
        if (error is null)
        {
            await next();
            return;
        }

        context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Result = ApiProblem.ToResult(context.HttpContext, error);
    }
}
