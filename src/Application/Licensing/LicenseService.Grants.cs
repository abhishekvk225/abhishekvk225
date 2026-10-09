using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.Licensing;

/// <summary>
/// Credit movements that come from a purchase rather than from a person at the admin console. They reuse the license service's own
/// transaction, ledger and audit machinery; only the ledger reason differs.
/// </summary>
public interface ILicenseGrantService
{
    /// <summary>
    /// Issues a license exactly like <see cref="ILicenseService.CreateAsync"/> but records <paramref name="ledgerReason"/> on the Grant
    /// ledger row and <paramref name="source"/> in the audit entry (for example the purchase it belongs to). Joins the caller's transaction.
    /// </summary>
    Task<Result<LicenseDto>> CreateGrantAsync(Guid clientId, CreateLicenseRequest request, string ledgerReason, string source, CancellationToken cancellationToken);

    /// <summary>
    /// Takes back up to <paramref name="maxCredits"/> of the license's UNUSED credits (a refund). Never goes below what was consumed, so the
    /// balance can not become negative; an expired or revoked license has nothing left to take. Returns how many credits were taken.
    /// Retried on a version conflict (a concurrent charge), like every other admin change.
    /// </summary>
    Task<Result<int>> RevokeUnusedCreditsAsync(Guid licenseId, int maxCredits, string reason, CancellationToken cancellationToken);
}

public sealed partial class LicenseService
{
    public async Task<Result<int>> RevokeUnusedCreditsAsync(Guid licenseId, int maxCredits, string reason, CancellationToken cancellationToken)
    {
        if (maxCredits <= 0)
        {
            return 0;
        }

        var taken = 0;
        var result = await InTransactionAsync(licenseId, async (license, ct) =>
        {
            taken = 0;
            if (license.Status is LicenseStatus.Revoked or LicenseStatus.Expired || license.Remaining <= 0)
            {
                return;
            }

            var n = Math.Min(maxCredits, license.Remaining);
            var before = license.Remaining;
            license.AdjustCredits(-n);
            await _unitOfWork.SaveChangesAsync(ct); // takes the row lock (and checks the version) before the ledger tail is read
            await _writer.AppendAsync(license, LedgerEntryType.Adjustment, -n, before, ct, reason: reason);
            _audit.Record(new AuditEntry("license.credits_revoked", nameof(License), license.Id.ToString(), license.ClientId,
                NewValues: new { Credits = -n, Reason = reason }));
            taken = n;
        }, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!;
        }

        return taken;
    }
}
