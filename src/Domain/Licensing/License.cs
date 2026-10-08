using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Licensing;

/// <summary>
/// A pack of credits granted to one client for a period. All lifecycle rules live here. Consumption itself is NOT a method on
/// this entity: it is a single atomic conditional UPDATE in the metering store so concurrent charges can never overdraw it.
/// </summary>
public sealed class License : AuditableEntity, ITenantOwned
{
    private License()
    {
    }

    public Guid ClientId { get; set; }

    public Guid? PlanId { get; private set; }

    public string LicenseKey { get; private set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public LicenseStatus Status { get; private set; }

    public int TotalCredits { get; private set; }

    public int ConsumedCredits { get; private set; }

    public DateTime StartsAt { get; private set; }

    public DateTime ExpiresAt { get; private set; }

    public DateTime? SuspendedAt { get; private set; }

    public string? SuspendedReason { get; private set; }

    public Guid? RenewedFromLicenseId { get; private set; }

    public string? Notes { get; set; }

    public byte[] RowVersion { get; private set; } = [];

    public int Remaining => TotalCredits - ConsumedCredits;

    public static License Create(Guid clientId, string name, Guid? planId, string licenseKey, int credits, DateTime startsAt, DateTime expiresAt)
    {
        if (credits < 0)
        {
            throw new DomainException("LICENSE_CREDITS_INVALID", "Credits cannot be negative.");
        }

        if (expiresAt <= startsAt)
        {
            throw new DomainException("LICENSE_PERIOD_INVALID", "The license must expire after it starts.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("LICENSE_NAME_REQUIRED", "A license name is required.");
        }

        return new License
        {
            ClientId = clientId,
            PlanId = planId,
            LicenseKey = licenseKey,
            Name = name.Trim(),
            Status = LicenseStatus.Active,
            TotalCredits = credits,
            StartsAt = startsAt,
            ExpiresAt = expiresAt,
        };
    }

    /// <summary>Usable right now for a charge of <paramref name="cost"/> credits (the same rule the metering SQL applies).</summary>
    public bool IsUsable(DateTime now, int cost = 1) =>
        Status == LicenseStatus.Active && StartsAt <= now && now < ExpiresAt && Remaining >= cost;

    /// <summary>The status a user should see: a license past its end date is Expired even if the sweeper has not flipped it yet.</summary>
    public LicenseStatus EffectiveStatus(DateTime now) =>
        (Status is LicenseStatus.Active or LicenseStatus.Inactive or LicenseStatus.Suspended or LicenseStatus.Draft) && now >= ExpiresAt
            ? LicenseStatus.Expired
            : Status;

    public void Activate(DateTime now)
    {
        if (Status is not (LicenseStatus.Draft or LicenseStatus.Inactive or LicenseStatus.Suspended))
        {
            throw Invalid($"A {Status} license cannot be activated.");
        }

        if (now >= ExpiresAt)
        {
            throw Invalid("The license has passed its end date; renew it instead.");
        }

        Status = LicenseStatus.Active;
        SuspendedAt = null;
        SuspendedReason = null;
    }

    public void Deactivate()
    {
        if (Status != LicenseStatus.Active)
        {
            throw Invalid($"Only an Active license can be deactivated (it is {Status}).");
        }

        Status = LicenseStatus.Inactive;
    }

    public void Suspend(string reason, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("LICENSE_REASON_REQUIRED", "A reason is required to suspend a license.");
        }

        if (Status != LicenseStatus.Active)
        {
            throw Invalid($"Only an Active license can be suspended (it is {Status}).");
        }

        Status = LicenseStatus.Suspended;
        SuspendedAt = now;
        SuspendedReason = reason.Trim();
    }

    /// <summary>Terminal. Unused credits are written off so the balance becomes zero. Returns the written-off amount.</summary>
    public int Revoke(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("LICENSE_REASON_REQUIRED", "A reason is required to revoke a license.");
        }

        if (Status == LicenseStatus.Revoked)
        {
            throw Invalid("The license is already revoked.");
        }

        var written = Remaining;
        TotalCredits = ConsumedCredits;
        Status = LicenseStatus.Revoked;
        SuspendedReason = reason.Trim();
        return written;
    }

    /// <summary>Marks a license that has passed its end date as Expired and writes off the unused credits. Returns the written-off amount, or null if nothing to do.</summary>
    public int? Expire(DateTime now)
    {
        if (now < ExpiresAt || Status is LicenseStatus.Expired or LicenseStatus.Revoked)
        {
            return null;
        }

        var written = Remaining;
        TotalCredits = ConsumedCredits;
        Status = LicenseStatus.Expired;
        return written;
    }

    /// <summary>Extends the license in place: a later end date and optionally more credits. An Expired license becomes Active again.</summary>
    public void Renew(DateTime newExpiresAt, int additionalCredits, DateTime now)
    {
        if (Status == LicenseStatus.Revoked)
        {
            throw Invalid("A revoked license cannot be renewed; issue a new one.");
        }

        if (additionalCredits < 0)
        {
            throw new DomainException("LICENSE_CREDITS_INVALID", "Additional credits cannot be negative.");
        }

        if (newExpiresAt <= now || newExpiresAt <= StartsAt || newExpiresAt <= ExpiresAt && Status != LicenseStatus.Expired)
        {
            throw new DomainException("LICENSE_PERIOD_INVALID", "The new end date must be in the future and later than the current one.");
        }

        ExpiresAt = newExpiresAt;
        TotalCredits += additionalCredits;
        if (Status == LicenseStatus.Expired)
        {
            Status = LicenseStatus.Active;
        }
    }

    /// <summary>Adds or removes credits (a correction, with a reason recorded in the ledger). The balance can never go below zero.</summary>
    public void AdjustCredits(int delta)
    {
        if (Status is LicenseStatus.Revoked or LicenseStatus.Expired)
        {
            throw Invalid($"Credits of a {Status} license cannot be adjusted.");
        }

        if (delta == 0)
        {
            throw new DomainException("LICENSE_CREDITS_INVALID", "The adjustment must not be zero.");
        }

        if (TotalCredits + delta < ConsumedCredits)
        {
            throw new DomainException("LICENSE_CREDITS_INVALID", "The adjustment would make the balance negative.");
        }

        TotalCredits += delta;
    }

    /// <summary>Gives back credits of an earlier consumption.</summary>
    public void Refund(int credits)
    {
        if (Status is not (LicenseStatus.Active or LicenseStatus.Inactive or LicenseStatus.Suspended))
        {
            throw Invalid($"A {Status} license cannot receive refunds.");
        }

        if (credits <= 0 || credits > ConsumedCredits)
        {
            throw new DomainException("LICENSE_CREDITS_INVALID", "The refund must be positive and not exceed what was consumed.");
        }

        ConsumedCredits -= credits;
    }

    private static DomainException Invalid(string message) => new("LICENSE_INVALID_TRANSITION", message);
}
