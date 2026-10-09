using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Licensing;

public enum LicenseAlertType
{
    LowBalance = 0,
    Exhausted,
    Expiring,
    Expired,
    ApiKeyExpiring,

    /// <summary>A top-up purchase was paid and its credits granted (the subject is the order).</summary>
    PaymentReceived,

    /// <summary>A purchase was refunded (the subject is the refund).</summary>
    PaymentRefunded,
}

public enum AlertSeverity
{
    Info = 0,
    Warning,
    Critical,
}

/// <summary>
/// One alert raised for a client (low credits, expiring or expired license, expiring API key). The unique
/// <c>(SubjectId, AlertType, Bucket)</c> index is what makes the alert job idempotent: a bucket names one threshold crossing
/// (for example "7 days before this end date"), so a restart or a second node can never send it twice. The same rows feed the
/// in-app notification list. Text never contains personal data.
/// </summary>
public sealed class LicenseAlert : Entity, ITenantOwned
{
    private LicenseAlert()
    {
    }

    public Guid ClientId { get; set; }

    /// <summary>The license (or API key, for <see cref="LicenseAlertType.ApiKeyExpiring"/>) the alert is about.</summary>
    public Guid SubjectId { get; private set; }

    public LicenseAlertType AlertType { get; private set; }

    /// <summary>Names the threshold crossing, e.g. <c>exp7d@20261201</c>; a renewal changes the bucket so alerts can fire again.</summary>
    public string Bucket { get; private set; } = string.Empty;

    public AlertSeverity Severity { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Message { get; private set; } = string.Empty;

    public DateTime CreatedAt { get; private set; }

    public DateTime? ReadAt { get; private set; }

    public static LicenseAlert Create(
        Guid clientId, LicenseAlertType type, Guid subjectId, string bucket, AlertSeverity severity, string title, string message, DateTime now) =>
        new()
        {
            ClientId = clientId,
            AlertType = type,
            SubjectId = subjectId,
            Bucket = bucket,
            Severity = severity,
            Title = title.Length > 150 ? title[..150] : title,
            Message = message.Length > 400 ? message[..400] : message,
            CreatedAt = now,
        };

    public void MarkRead(DateTime now) => ReadAt ??= now;
}
