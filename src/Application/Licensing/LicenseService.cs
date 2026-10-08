using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.Licensing;

public interface ILicenseService
{
    Task<Result<PagedResult<LicenseListItemDto>>> ListAsync(LicenseListQuery query, CancellationToken cancellationToken);

    Task<Result<LicenseDto>> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<LicenseDto>> CreateAsync(Guid clientId, CreateLicenseRequest request, CancellationToken cancellationToken);

    Task<Result<LicenseDto>> UpdateAsync(Guid id, UpdateLicenseRequest request, CancellationToken cancellationToken);

    Task<Result<LicenseDto>> ActivateAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<LicenseDto>> DeactivateAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<LicenseDto>> SuspendAsync(Guid id, LicenseReasonRequest request, CancellationToken cancellationToken);

    Task<Result<LicenseDto>> RevokeAsync(Guid id, LicenseReasonRequest request, CancellationToken cancellationToken);

    Task<Result<LicenseDto>> RenewAsync(Guid id, RenewLicenseRequest request, CancellationToken cancellationToken);

    /// <summary>Applies the adjustment, or — above the per-action cap — files a request for a second person to approve.</summary>
    Task<Result<AdjustOutcome>> AdjustAsync(Guid id, AdjustLicenseRequest request, CancellationToken cancellationToken);

    Task<Result<LicenseTransactionDto>> RefundAsync(long transactionId, RefundRequest request, CancellationToken cancellationToken);

    Task<Result<PagedResult<LicenseTransactionDto>>> GetTransactionsAsync(Guid id, PageRequest page, CancellationToken cancellationToken);

    Task<Result<LedgerVerificationDto>> VerifyLedgerAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>
/// Platform-side license administration. Every change that moves credits writes an immutable ledger row in the same
/// transaction as the license change, so the balance and its history can never disagree.
/// </summary>
public sealed partial class LicenseService : ILicenseService, ILicenseAdjustmentService
{
    private readonly ILicenseRepository _licenses;
    private readonly ILedgerRepository _ledger;
    private readonly IPlanRepository _plans;
    private readonly IClientRepository _clients;
    private readonly LedgerWriter _writer;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;
    private readonly ILicenseAdjustmentRepository _adjustments;
    private readonly ILicenseAdjustmentAtomics _adjustmentAtomics;
    private readonly ICurrentUser _currentUser;
    private readonly LicensingOptions _licensing;

    public LicenseService(
        ILicenseRepository licenses,
        ILedgerRepository ledger,
        IPlanRepository plans,
        IClientRepository clients,
        LedgerWriter writer,
        IAuditService audit,
        IUnitOfWork unitOfWork,
        TimeProvider time,
        ILicenseAdjustmentRepository adjustments,
        ILicenseAdjustmentAtomics adjustmentAtomics,
        ICurrentUser currentUser,
        IOptions<LicensingOptions> licensing)
    {
        _adjustments = adjustments;
        _adjustmentAtomics = adjustmentAtomics;
        _currentUser = currentUser;
        _licensing = licensing.Value;
        _licenses = licenses;
        _ledger = ledger;
        _plans = plans;
        _clients = clients;
        _writer = writer;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _time = time;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<Result<PagedResult<LicenseListItemDto>>> ListAsync(LicenseListQuery query, CancellationToken cancellationToken)
    {
        LicenseStatus? status = null;
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<LicenseStatus>(query.Status, ignoreCase: true, out var parsed))
            {
                return Error.Validation("Unknown status filter.");
            }

            status = parsed;
        }

        var paging = new PageRequest { Page = query.Page, PageSize = query.PageSize }.Normalize();
        var now = Now;
        var (items, total) = await _licenses.ListAsync(query.ClientId, status, query.ExpiringInDays, query.Search, now, paging.Skip, paging.PageSize, cancellationToken);
        return new PagedResult<LicenseListItemDto>(items.Select(i => i.ToListItem(now)).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<Result<LicenseDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await _licenses.GetRowAsync(id, cancellationToken);
        return row is null ? Error.NotFound() : row.ToDto(Now);
    }

    public async Task<Result<LicenseDto>> CreateAsync(Guid clientId, CreateLicenseRequest request, CancellationToken cancellationToken)
    {
        var client = await _clients.GetByIdAsync(clientId, cancellationToken);
        if (client is null || client.IsSystem)
        {
            return Error.NotFound("The client was not found.");
        }

        Plan? plan = null;
        if (request.PlanId is { } planId)
        {
            plan = await _plans.GetByIdAsync(planId, cancellationToken);
            if (plan is null || !plan.IsActive)
            {
                return Error.Validation("The plan was not found or is inactive.", new Dictionary<string, string[]> { ["planId"] = ["Unknown plan."] });
            }
        }

        var now = Now;
        var credits = request.TotalCredits ?? plan?.DefaultCredits;
        if (credits is null)
        {
            return Error.Validation("Total credits are required when no plan is given.", new Dictionary<string, string[]> { ["totalCredits"] = ["Required."] });
        }

        var starts = request.StartsAt is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : now;
        DateTime? expires = request.ExpiresAt is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : plan is null ? null : starts.AddDays(plan.DefaultDurationDays);
        if (expires is null)
        {
            return Error.Validation("An end date is required when no plan is given.", new Dictionary<string, string[]> { ["expiresAt"] = ["Required."] });
        }

        License license;
        try
        {
            license = License.Create(clientId, request.Name, plan?.Id, await NewKeyAsync(cancellationToken), credits.Value, starts, expires.Value);
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        license.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        _licenses.Add(license);
        await _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                await _writer.AppendAsync(license, LedgerEntryType.Grant, license.TotalCredits, 0, ct, reason: "License issued");
                _audit.Record(new AuditEntry("license.created", nameof(License), license.Id.ToString(), clientId,
                    NewValues: new { license.LicenseKey, license.Name, license.TotalCredits, license.StartsAt, license.ExpiresAt, PlanId = plan?.Id }));
                await _unitOfWork.SaveChangesAsync(ct);
                return true;
            },
            cancellationToken);

        return (await _licenses.GetRowAsync(license.Id, cancellationToken))!.ToDto(Now);
    }

    public async Task<Result<LicenseDto>> UpdateAsync(Guid id, UpdateLicenseRequest request, CancellationToken cancellationToken)
    {
        var license = await _licenses.GetByIdAsync(id, cancellationToken);
        if (license is null)
        {
            return Error.NotFound();
        }

        if (!TryVersion(request.RowVersion, out var version))
        {
            return Error.Validation("rowVersion is not valid.", new Dictionary<string, string[]> { ["rowVersion"] = ["Invalid concurrency token."] });
        }

        _licenses.SetExpectedVersion(license, version);
        var before = new { license.Name, license.Notes };
        license.Name = request.Name.Trim();
        license.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        _audit.Record(new AuditEntry("license.updated", nameof(License), license.Id.ToString(), license.ClientId, OldValues: before, NewValues: new { license.Name, license.Notes }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return await ReloadAsync(id, cancellationToken);
    }

    public Task<Result<LicenseDto>> ActivateAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, "license.activated", (l, now) => l.Activate(now), cancellationToken);

    public Task<Result<LicenseDto>> DeactivateAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, "license.deactivated", (l, _) => l.Deactivate(), cancellationToken);

    public Task<Result<LicenseDto>> SuspendAsync(Guid id, LicenseReasonRequest request, CancellationToken cancellationToken) =>
        TransitionAsync(id, "license.suspended", (l, now) => l.Suspend(request.Reason ?? string.Empty, now), cancellationToken, request.Reason);

    public Task<Result<LicenseDto>> RevokeAsync(Guid id, LicenseReasonRequest request, CancellationToken cancellationToken) =>
        InTransactionAsync(id, async (license, ct) =>
        {
            var before = license.Remaining;
            var written = license.Revoke(request.Reason ?? string.Empty);
            await _unitOfWork.SaveChangesAsync(ct); // takes the row lock (and checks the version) before the ledger tail is read
            if (written > 0)
            {
                await _writer.AppendAsync(license, LedgerEntryType.Revocation, -written, before, ct, reason: request.Reason);
            }

            _audit.Record(new AuditEntry("license.revoked", nameof(License), license.Id.ToString(), license.ClientId, NewValues: new { request.Reason, WrittenOff = written }));
        }, cancellationToken);

    public Task<Result<LicenseDto>> RenewAsync(Guid id, RenewLicenseRequest request, CancellationToken cancellationToken)
    {
        var now = Now;
        var newEnd = DateTime.SpecifyKind(request.ExpiresAt, DateTimeKind.Utc);
        return InTransactionAsync(id, async (license, ct) =>
        {
            var before = license.Remaining;
            var oldEnd = license.ExpiresAt;
            license.Renew(newEnd, request.AdditionalCredits, now);
            await _unitOfWork.SaveChangesAsync(ct);
            // Always record the renewal, even when no credits were added: the ledger is the audit trail for the period too.
            await _writer.AppendAsync(license, LedgerEntryType.Renewal, request.AdditionalCredits, before, ct,
                reason: request.Reason ?? $"Renewed until {newEnd:yyyy-MM-dd}");
            _audit.Record(new AuditEntry("license.renewed", nameof(License), license.Id.ToString(), license.ClientId,
                OldValues: new { ExpiresAt = oldEnd }, NewValues: new { ExpiresAt = newEnd, request.AdditionalCredits, request.Reason }));
        }, cancellationToken);
    }

    public async Task<Result<LicenseTransactionDto>> RefundAsync(long transactionId, RefundRequest request, CancellationToken cancellationToken)
    {
        var original = await _ledger.GetAsync(transactionId, cancellationToken);
        if (original is null)
        {
            return Error.NotFound();
        }

        if (original.Type != LedgerEntryType.Consume)
        {
            return Error.Conflict(ErrorCodes.Conflict, "Only a consumption can be refunded.");
        }

        try
        {
            return await WithConflictRetryAsync(
                () => _unitOfWork.ExecuteInTransactionAsync<Result<LicenseTransactionDto>>(
                    async ct =>
                    {
                        // The unique index on (ReferenceTransactionId) for refunds is the real guard; this check gives a clean error first.
                        if (await _ledger.HasRefundForAsync(transactionId, ct))
                        {
                            return Error.Conflict(ErrorCodes.Conflict, "This charge has already been refunded.");
                        }

                        var license = await _licenses.GetByIdAsync(original.LicenseId, ct);
                        if (license is null)
                        {
                            return Error.NotFound();
                        }

                        var before = license.Remaining;
                        license.Refund(-original.Credits);
                        await _unitOfWork.SaveChangesAsync(ct);
                        var entry = await _writer.AppendAsync(license, LedgerEntryType.Refund, -original.Credits, before, ct,
                            operation: original.Operation, referenceTransactionId: original.Id, reason: request.Reason);
                        _audit.Record(new AuditEntry("license.refunded", nameof(License), license.Id.ToString(), license.ClientId,
                            NewValues: new { TransactionId = transactionId, Credits = -original.Credits, request.Reason }));
                        await _unitOfWork.SaveChangesAsync(ct);
                        return entry.ToDto();
                    },
                    cancellationToken));
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }
        catch (UniqueConstraintViolationException)
        {
            return Error.Conflict(ErrorCodes.Conflict, "This charge has already been refunded.");
        }
    }

    public async Task<Result<PagedResult<LicenseTransactionDto>>> GetTransactionsAsync(Guid id, PageRequest page, CancellationToken cancellationToken)
    {
        if (await _licenses.GetByIdAsync(id, cancellationToken) is null)
        {
            return Error.NotFound();
        }

        var paging = page.Normalize();
        var (items, total) = await _ledger.ListAsync(id, paging.Skip, paging.PageSize, cancellationToken);
        return new PagedResult<LicenseTransactionDto>(items.Select(t => t.ToDto()).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<Result<LedgerVerificationDto>> VerifyLedgerAsync(Guid id, CancellationToken cancellationToken)
    {
        var license = await _licenses.GetByIdAsync(id, cancellationToken);
        if (license is null)
        {
            return Error.NotFound();
        }

        var entries = await _ledger.GetAllAsync(id, cancellationToken);
        return LedgerVerifier.Verify(license, entries);
    }

    private Task<Result<LicenseDto>> TransitionAsync(Guid id, string action, Action<License, DateTime> change, CancellationToken cancellationToken, string? reason = null)
    {
        var now = Now;
        return InTransactionAsync(id, (license, _) =>
        {
            var before = license.Status;
            change(license, now);
            _audit.Record(new AuditEntry(action, nameof(License), license.Id.ToString(), license.ClientId,
                OldValues: new { Status = before.ToString() }, NewValues: new { Status = license.Status.ToString(), Reason = reason }));
            return Task.CompletedTask;
        }, cancellationToken);
    }

    /// <summary>
    /// Applies a domain change plus its ledger/audit rows atomically; domain rule violations become API errors. The license is
    /// loaded INSIDE the transaction and the whole unit is retried on a version conflict, because every metered charge bumps the
    /// license's row version and would otherwise make admin changes fail while the client is being served.
    /// </summary>
    private async Task<Result<LicenseDto>> InTransactionAsync(Guid id, Func<License, CancellationToken, Task> change, CancellationToken cancellationToken)
    {
        try
        {
            var found = await WithConflictRetryAsync(() => _unitOfWork.ExecuteInTransactionAsync(
                async ct =>
                {
                    var license = await _licenses.GetByIdAsync(id, ct);
                    if (license is null)
                    {
                        return false;
                    }

                    await change(license, ct);
                    await _unitOfWork.SaveChangesAsync(ct);
                    return true;
                },
                cancellationToken));
            if (!found)
            {
                return Error.NotFound();
            }
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        return await ReloadAsync(id, cancellationToken);
    }

    private async Task<T> WithConflictRetryAsync<T>(Func<Task<T>> attempt)
    {
        const int maxAttempts = 12;
        for (var i = 1; ; i++)
        {
            try
            {
                return await attempt();
            }
            catch (ConcurrencyConflictException) when (i < maxAttempts)
            {
                _unitOfWork.ClearTracked(); // drop the stale entity so the retry reads current state
                await Task.Delay(Random.Shared.Next(5, 40 + (i * 10))); // jitter so competing admin writers don't collide in lockstep
            }
        }
    }

    private async Task<Result<LicenseDto>> ReloadAsync(Guid id, CancellationToken cancellationToken) =>
        (await _licenses.GetRowAsync(id, cancellationToken))!.ToDto(Now);

    private async Task<string> NewKeyAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var key = LicenseKeyGenerator.Generate();
            if (!await _licenses.KeyExistsAsync(key, cancellationToken))
            {
                return key;
            }
        }

        throw new InvalidOperationException("Could not generate a unique license key.");
    }

    private static bool TryVersion(string value, out byte[] version)
    {
        try
        {
            version = Convert.FromBase64String(value);
            return version.Length > 0;
        }
        catch (FormatException)
        {
            version = [];
            return false;
        }
    }
}
