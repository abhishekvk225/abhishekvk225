using NexaVerify.Application.Common;

namespace NexaVerify.Application.Auditing;

/// <summary>What happened. Old/new values are redacted before storage (secrets, hashes and biometric fields are never written).</summary>
public sealed record AuditEntry(
    string Action,
    string EntityType,
    string EntityId,
    Guid? ClientId = null,
    object? OldValues = null,
    object? NewValues = null,
    Guid? ActorId = null);

public interface IAuditService
{
    /// <summary>Stages an audit row in the current unit of work; it is committed atomically with the business change.</summary>
    void Record(AuditEntry entry);
}

public static class AuditActions
{
    public const string UserCreated = "user.created";
    public const string UserUpdated = "user.updated";
    public const string UserDeactivated = "user.deactivated";
    public const string PasswordChanged = "user.password_changed";
    public const string PasswordResetRequested = "user.password_reset_requested";
    public const string PasswordReset = "user.password_reset";
    public const string SessionReuseDetected = "session.refresh_reuse_detected";
    public const string RoleCreated = "role.created";
    public const string RoleUpdated = "role.updated";
}
