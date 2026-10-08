using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Licensing;

public enum AdjustmentStatus
{
    Pending = 0,
    Approved,
    Rejected,
    Expired,
}

/// <summary>
/// A credit adjustment above the per-action cap waits here until a DIFFERENT person with the approval permission accepts it
/// within the approval window. Only the approval writes the ledger entry. Every transition is also written to the immutable audit
/// log, so the decision trail survives even though this row's status changes.
/// </summary>
public sealed class LicenseAdjustmentRequest : Entity, ITenantOwned
{
    private LicenseAdjustmentRequest()
    {
    }

    public Guid ClientId { get; set; }

    public Guid LicenseId { get; private set; }

    /// <summary>Signed change of the license balance.</summary>
    public int Credits { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public Guid RequestedBy { get; private set; }

    public DateTime RequestedAt { get; private set; }

    public DateTime ExpiresAt { get; private set; }

    public AdjustmentStatus Status { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTime? DecidedAt { get; private set; }

    public string? DecisionNote { get; private set; }

    /// <summary>The ledger row written by the approval.</summary>
    public long? LedgerTransactionId { get; private set; }

    public static LicenseAdjustmentRequest Create(License license, int credits, string reason, Guid requestedBy, DateTime now, TimeSpan validFor)
    {
        if (credits == 0)
        {
            throw new DomainException("LICENSE_CREDITS_INVALID", "The adjustment must not be zero.");
        }

        return new LicenseAdjustmentRequest
        {
            ClientId = license.ClientId,
            LicenseId = license.Id,
            Credits = credits,
            Reason = reason.Length > 500 ? reason[..500] : reason,
            RequestedBy = requestedBy,
            RequestedAt = now,
            ExpiresAt = now.Add(validFor),
            Status = AdjustmentStatus.Pending,
        };
    }

    /// <summary>What a reader should see now: a pending request past its deadline is Expired even before anything has rewritten the row.</summary>
    public AdjustmentStatus EffectiveStatus(DateTime now) => Status == AdjustmentStatus.Pending && ExpiresAt <= now ? AdjustmentStatus.Expired : Status;

    public bool IsOpen(DateTime now) => Status == AdjustmentStatus.Pending && ExpiresAt > now;
}
