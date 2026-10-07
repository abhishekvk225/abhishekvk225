using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Tenancy;

public enum ClientStatus
{
    PendingActivation = 0,
    Active,
    Inactive,
    Suspended,
    Deleted,
}

/// <summary>A customer organisation (tenant). Lifecycle rules live here so every code path obeys them.</summary>
public sealed class Client : AuditableEntity, ITenantRoot
{
    private Client()
    {
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? LegalName { get; set; }

    public string ContactEmail { get; set; } = string.Empty;

    public string? ContactPhone { get; set; }

    public string? AddressLine1 { get; set; }

    public string? AddressLine2 { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? PostalCode { get; set; }

    /// <summary>ISO 3166-1 alpha-2.</summary>
    public string? Country { get; set; }

    public string? Website { get; set; }

    public string? Industry { get; set; }

    /// <summary>IANA time zone id used to render dates for this client's users.</summary>
    public string TimeZone { get; set; } = "UTC";

    public ClientStatus Status { get; private set; } = ClientStatus.Active;

    public string? StatusReason { get; private set; }

    public DateTime? StatusChangedAt { get; private set; }

    public Guid? StatusChangedBy { get; private set; }

    public string? Notes { get; set; }

    /// <summary>The seeded platform pseudo-client that owns platform-level rows. Never listed, never editable.</summary>
    public bool IsSystem { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static Client Create(string code, string name, string contactEmail, string timeZone, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 30 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new DomainException("CLIENT_CODE_INVALID", "Client code must be 1–30 letters, digits or hyphens.");
        }

        return new Client
        {
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            ContactEmail = contactEmail.Trim(),
            TimeZone = timeZone,
            Status = ClientStatus.Active,
            StatusChangedAt = now,
        };
    }

    /// <summary>The platform pseudo-client (fixed id) that owns platform-level rows.</summary>
    public static Client CreateSystem(DateTime now) => new()
    {
        Code = "PLATFORM",
        Name = "NexaVerify Platform",
        ContactEmail = "platform@nexaverify.invalid",
        Status = ClientStatus.Active,
        StatusChangedAt = now,
        IsSystem = true,
        IdOverride = PlatformTenant.ClientId,
    };

    private Guid IdOverride
    {
        set => Id = value;
    }

    public bool CanSignIn => Status == ClientStatus.Active;

    public void Activate(Guid? actor, DateTime now) => Transition(ClientStatus.Active, null, actor, now);

    public void Deactivate(string? reason, Guid? actor, DateTime now) => Transition(ClientStatus.Inactive, reason, actor, now);

    public void Suspend(string reason, Guid? actor, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("CLIENT_SUSPEND_REASON_REQUIRED", "A reason is required to suspend a client.");
        }

        Transition(ClientStatus.Suspended, reason.Trim(), actor, now);
    }

    private void Transition(ClientStatus target, string? reason, Guid? actor, DateTime now)
    {
        if (IsSystem)
        {
            throw new DomainException("CLIENT_SYSTEM_IMMUTABLE", "The platform client cannot be changed.");
        }

        if (Status == target)
        {
            throw new DomainException("CLIENT_INVALID_TRANSITION", $"The client is already {target}.");
        }

        if (Status == ClientStatus.Deleted)
        {
            throw new DomainException("CLIENT_INVALID_TRANSITION", "A deleted client cannot change status.");
        }

        Status = target;
        StatusReason = reason;
        StatusChangedAt = now;
        StatusChangedBy = actor;
    }
}
