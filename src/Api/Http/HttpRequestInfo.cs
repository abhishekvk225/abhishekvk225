using NexaVerify.Api.Middleware;
using NexaVerify.Application.Abstractions;

namespace NexaVerify.Api.Http;

public sealed class HttpRequestInfo : IRequestInfo
{
    private readonly IHttpContextAccessor _accessor;

    public HttpRequestInfo(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    public string? IpAddress => _accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => _accessor.HttpContext?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;

    public string? CorrelationId => _accessor.HttpContext is { } http ? CorrelationIdMiddleware.GetCorrelationId(http) : null;
}
