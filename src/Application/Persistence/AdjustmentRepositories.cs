using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.Persistence;

public interface ILicenseAdjustmentRepository
{
    Task<LicenseAdjustmentRequest?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<(IReadOnlyList<LicenseAdjustmentRequest> Items, int Total)> ListAsync(
        AdjustmentStatus? status, Guid? licenseId, DateTime now, int skip, int take, CancellationToken cancellationToken);

    void Add(LicenseAdjustmentRequest request);
}

/// <summary>Conditional single-statement transitions: exactly one concurrent caller can decide a request.</summary>
public interface ILicenseAdjustmentAtomics
{
    /// <summary>Pending and inside its window → <paramref name="outcome"/>. True only for the one caller that won.</summary>
    Task<bool> TryDecideAsync(Guid id, AdjustmentStatus outcome, Guid decidedBy, string? note, DateTime now, CancellationToken cancellationToken);

    /// <summary>Pending and past its deadline → Expired.</summary>
    Task<bool> TryExpireAsync(Guid id, DateTime now, CancellationToken cancellationToken);

    Task SetLedgerEntryAsync(Guid id, long ledgerTransactionId, CancellationToken cancellationToken);
}
