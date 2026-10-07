using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Identity;

/// <summary>Every sign-in attempt, successful or not. Append-only.</summary>
public sealed class LoginHistory : ITenantOwned, IAppendOnly
{
    public long Id { get; private set; }

    public Guid ClientId { get; set; }

    public Guid? UserId { get; init; }

    /// <summary>The email as typed (truncated); kept for unknown-user attempts so brute force is visible.</summary>
    public string EmailAttempted { get; init; } = string.Empty;

    public LoginOutcome Outcome { get; init; }

    public string? FailureReason { get; init; }

    public string? IpAddress { get; init; }

    public string? UserAgent { get; init; }

    public string? CorrelationId { get; init; }

    public DateTime OccurredAt { get; init; }
}
