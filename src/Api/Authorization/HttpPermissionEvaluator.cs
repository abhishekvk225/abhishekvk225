using Microsoft.AspNetCore.Authorization;
using NexaVerify.Application.Abstractions;

namespace NexaVerify.Api.Authorization;

/// <summary>
/// Answers "does this caller hold this permission?" with the very policy <c>[HasPermission]</c> uses (roles, API-key scopes, platform/client
/// scope rules), for application services whose decision depends on data. Outside an HTTP request the answer is false.
/// </summary>
public sealed class HttpPermissionEvaluator : IPermissionEvaluator
{
    private readonly IAuthorizationService _authorization;
    private readonly IHttpContextAccessor _accessor;

    public HttpPermissionEvaluator(IAuthorizationService authorization, IHttpContextAccessor accessor)
    {
        _authorization = authorization;
        _accessor = accessor;
    }

    public async Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken)
    {
        if (_accessor.HttpContext?.User is not { Identity.IsAuthenticated: true } user)
        {
            return false;
        }

        var result = await _authorization.AuthorizeAsync(user, null, HasPermissionAttribute.PolicyPrefix + permission).ConfigureAwait(false);
        return result.Succeeded;
    }
}
