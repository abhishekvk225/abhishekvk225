using NexaVerify.Application.Abstractions;

namespace NexaVerify.Infrastructure.Tenancy;

/// <summary>Request context for code running outside an HTTP request (jobs, migrator).</summary>
public sealed class NullRequestInfo : IRequestInfo
{
    public string? IpAddress => null;

    public string? UserAgent => null;

    public string? CorrelationId => null;
}
