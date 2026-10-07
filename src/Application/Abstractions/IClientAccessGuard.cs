using NexaVerify.Application.Common;

namespace NexaVerify.Application.Abstractions;

/// <summary>Decides whether a client may currently sign in / call the API (active vs inactive/suspended). Implemented by the tenancy module.</summary>
public interface IClientAccessGuard
{
    /// <summary>Returns null when access is allowed, otherwise the error to return to the caller.</summary>
    Task<Error?> CheckAsync(Guid clientId, CancellationToken cancellationToken);

    /// <summary>Drops any cached status so a change (suspend/activate) applies on the very next request.</summary>
    void Invalidate(Guid clientId);
}
