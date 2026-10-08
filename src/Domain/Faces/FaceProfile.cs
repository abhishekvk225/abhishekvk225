using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Faces;

/// <summary>
/// A person registered by a client. Biometric data: visible to the owning client only (never across tenants, not even to the
/// platform), with the consent that justified collecting it and an optional retention deadline.
/// </summary>
public sealed class FaceProfile : AuditableEntity, IStrictTenantOwned
{
    public const int MaxTemplates = 5;

    private FaceProfile()
    {
    }

    public Guid ClientId { get; set; }

    /// <summary>The client's own identifier for the person.</summary>
    public string ExternalRef { get; private set; } = string.Empty;

    /// <summary>Display name, encrypted under the client's key.</summary>
    public byte[]? DisplayNameEnc { get; set; }

    /// <summary>Flat client-defined key/values (JSON object, ≤ 4 KB).</summary>
    public string? MetadataJson { get; set; }

    public FaceProfileStatus Status { get; private set; }

    public string ConsentReference { get; private set; } = string.Empty;

    public DateTime ConsentRecordedAt { get; private set; }

    public DateTime? RetentionUntil { get; set; }

    public byte[] RowVersion { get; private set; } = [];

    public static FaceProfile Create(Guid clientId, string externalRef, string consentReference, DateTime now, DateTime? retentionUntil)
    {
        var reference = (externalRef ?? string.Empty).Trim();
        if (reference.Length is 0 or > 100)
        {
            throw new DomainException("EXTERNAL_REF_INVALID", "External reference must be 1–100 characters.");
        }

        var consent = (consentReference ?? string.Empty).Trim();
        if (consent.Length is 0 or > 200)
        {
            throw new DomainException("CONSENT_REQUIRED", "A consent reference (1–200 characters) is required to register a face.");
        }

        return new FaceProfile
        {
            ClientId = clientId,
            ExternalRef = reference,
            ConsentReference = consent,
            ConsentRecordedAt = now,
            RetentionUntil = retentionUntil,
            Status = FaceProfileStatus.Active,
        };
    }

    public void Disable() => Status = FaceProfileStatus.Disabled;

    public void Enable() => Status = FaceProfileStatus.Active;
}
