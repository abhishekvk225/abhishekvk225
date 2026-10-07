using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Auditing;

public enum AuditActorType
{
    User = 0,
    ApiKey,
    System,
}

/// <summary>A business event for the audit trail (who did what to which entity). Append-only; values are pre-redacted.</summary>
public sealed class AuditLog : ITenantOwned, IAppendOnly
{
    public long Id { get; private set; }

    /// <summary>The client the event concerns; <see cref="PlatformTenant.ClientId"/> for platform-level events.</summary>
    public Guid ClientId { get; set; }

    public AuditActorType ActorType { get; init; }

    public Guid? ActorId { get; init; }

    public string? ActorEmail { get; init; }

    public string Action { get; init; } = string.Empty;

    public string EntityType { get; init; } = string.Empty;

    public string EntityId { get; init; } = string.Empty;

    public string? OldValuesJson { get; init; }

    public string? NewValuesJson { get; init; }

    public string? IpAddress { get; init; }

    public string? UserAgent { get; init; }

    public string? CorrelationId { get; init; }

    public DateTime OccurredAt { get; init; }
}
