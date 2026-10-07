namespace NexaVerify.Domain.Common;

/// <summary>
/// Audit columns are stamped by the persistence layer on save (never set by business code).
/// All timestamps are UTC.
/// </summary>
public abstract class AuditableEntity : Entity
{
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsActive { get; set; } = true;
}
