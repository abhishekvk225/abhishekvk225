using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Common;

namespace NexaVerify.Infrastructure.Identity;

/// <summary>Placeholder until the tenancy module (M3) provides the real client-status guard.</summary>
public sealed class AllowAllClientAccessGuard : IClientAccessGuard
{
    public Task<Error?> CheckAsync(Guid clientId, CancellationToken cancellationToken) => Task.FromResult<Error?>(null);
}
