using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.Licensing;

/// <summary>Either the adjustment was applied (<see cref="License"/>) or it is waiting for a second approver (<see cref="PendingApproval"/>).</summary>
public sealed record AdjustOutcome(LicenseDto? License, AdjustmentRequestDto? PendingApproval);

public interface ILicenseAdjustmentService
{
    Task<Result<PagedResult<AdjustmentRequestDto>>> ListAsync(AdjustmentListQuery query, CancellationToken cancellationToken);

    Task<Result<AdjustmentRequestDto>> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Approves and applies a request. The approver must differ from the requester; only now is the ledger entry written.</summary>
    Task<Result<AdjustmentRequestDto>> ApproveAsync(Guid id, ApproveAdjustmentRequest request, CancellationToken cancellationToken);

    Task<Result<AdjustmentRequestDto>> RejectAsync(Guid id, RejectAdjustmentRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Large credit corrections need two people. Anyone with <c>licenses.adjust</c> can ask; someone else with
/// <c>licenses.approve-adjust</c> decides within the approval window. The approval runs through the same atomic path as a small
/// adjustment (license change + ledger row in one transaction), so the ledger only ever contains approved amounts.
/// </summary>
public sealed partial class LicenseService
{
    public async Task<Result<AdjustOutcome>> AdjustAsync(Guid id, AdjustLicenseRequest request, CancellationToken cancellationToken)
    {
        if (Math.Abs((long)request.Credits) <= _licensing.MaxAdjustPerAction)
        {
            var applied = await ApplyAdjustmentAsync(id, request.Credits, request.Reason, requestId: null, requestedBy: null, cancellationToken);
            return applied.IsSuccess ? new AdjustOutcome(applied.Value, null) : applied.Error!;
        }

        if (_currentUser.ActorId is not { } requester || _currentUser.ActorType != ActorType.User)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only a signed-in staff member can request an adjustment.");
        }

        var license = await _licenses.GetByIdAsync(id, cancellationToken);
        if (license is null)
        {
            return Error.NotFound();
        }

        var now = Now;
        LicenseAdjustmentRequest pending;
        try
        {
            if (license.Status is LicenseStatus.Revoked or LicenseStatus.Expired)
            {
                throw new DomainException("LICENSE_INVALID_TRANSITION", $"Credits of a {license.Status} license cannot be adjusted.");
            }

            pending = LicenseAdjustmentRequest.Create(license, request.Credits, request.Reason.Trim(), requester, now, TimeSpan.FromHours(_licensing.AdjustApprovalHours));
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        _adjustments.Add(pending);
        _audit.Record(new AuditEntry("license.adjustment_requested", nameof(LicenseAdjustmentRequest), pending.Id.ToString(), license.ClientId,
            NewValues: new { LicenseId = license.Id, request.Credits, pending.Reason, pending.ExpiresAt }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return new AdjustOutcome(null, ToDto(pending, now));
    }

    async Task<Result<PagedResult<AdjustmentRequestDto>>> ILicenseAdjustmentService.ListAsync(AdjustmentListQuery query, CancellationToken cancellationToken)
    {
        AdjustmentStatus? status = null;
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<AdjustmentStatus>(query.Status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Error.Validation("Unknown status filter.");
            }

            status = parsed;
        }

        var now = Now;
        var paging = new PageRequest { Page = query.Page, PageSize = query.PageSize }.Normalize();
        var (items, total) = await _adjustments.ListAsync(status, query.LicenseId, now, paging.Skip, paging.PageSize, cancellationToken);
        return new PagedResult<AdjustmentRequestDto>(items.Select(r => ToDto(r, now)).ToList(), paging.Page, paging.PageSize, total);
    }

    async Task<Result<AdjustmentRequestDto>> ILicenseAdjustmentService.GetAsync(Guid id, CancellationToken cancellationToken) =>
        await _adjustments.GetAsync(id, cancellationToken) is { } found ? ToDto(found, Now) : Error.NotFound();

    public async Task<Result<AdjustmentRequestDto>> ApproveAsync(Guid id, ApproveAdjustmentRequest request, CancellationToken cancellationToken)
    {
        if (_currentUser.ActorId is not { } approver || _currentUser.ActorType != ActorType.User)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only a signed-in staff member can approve an adjustment.");
        }

        var pending = await _adjustments.GetAsync(id, cancellationToken);
        if (pending is null)
        {
            return Error.NotFound();
        }

        if (pending.RequestedBy == approver)
        {
            return Error.Forbidden(ErrorCodes.SelfApprovalForbidden, "You cannot approve your own adjustment request. Another approver must do it.");
        }

        var now = Now;
        if (await ExpiredResultAsync(pending, now, cancellationToken) is { } expired)
        {
            return expired;
        }

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var applied = await ApplyAdjustmentAsync(pending.LicenseId, pending.Credits, pending.Reason, pending.Id, pending.RequestedBy, cancellationToken, decisionNote: note);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        return ToDto((await _adjustments.GetAsync(id, cancellationToken))!, Now);
    }

    public async Task<Result<AdjustmentRequestDto>> RejectAsync(Guid id, RejectAdjustmentRequest request, CancellationToken cancellationToken)
    {
        if (_currentUser.ActorId is not { } approver || _currentUser.ActorType != ActorType.User)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only a signed-in staff member can reject an adjustment.");
        }

        var pending = await _adjustments.GetAsync(id, cancellationToken);
        if (pending is null)
        {
            return Error.NotFound();
        }

        var now = Now;
        if (await ExpiredResultAsync(pending, now, cancellationToken) is { } expired)
        {
            return expired;
        }

        // The requester may withdraw their own request (that is a rejection by the same person, which is harmless); only approval needs a second person.
        var won = await _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                if (!await _adjustmentAtomics.TryDecideAsync(id, AdjustmentStatus.Rejected, approver, request.Reason.Trim(), now, ct))
                {
                    return false;
                }

                _audit.Record(new AuditEntry("license.adjustment_rejected", nameof(LicenseAdjustmentRequest), id.ToString(), pending.ClientId,
                    NewValues: new { pending.LicenseId, pending.Credits, Reason = request.Reason.Trim(), pending.RequestedBy }));
                await _unitOfWork.SaveChangesAsync(ct);
                return true;
            },
            cancellationToken);
        if (!won)
        {
            return NotPending();
        }

        return ToDto((await _adjustments.GetAsync(id, cancellationToken))!, Now);
    }

    /// <summary>
    /// The one atomic adjustment path (license change + ledger row + audit in one transaction, retried on a version conflict).
    /// When it carries a request id, that request is claimed first in the same transaction: a second approver, a double click or a
    /// late approval finds it no longer pending and nothing is applied twice.
    /// </summary>
    private async Task<Result<LicenseDto>> ApplyAdjustmentAsync(
        Guid licenseId, int credits, string reason, Guid? requestId, Guid? requestedBy, CancellationToken cancellationToken, string? decisionNote = null)
    {
        var now = Now;
        try
        {
            return await InTransactionAsync(licenseId, async (license, ct) =>
            {
                if (requestId is { } rid
                    && (_currentUser.ActorId is not { } approver
                        || !await _adjustmentAtomics.TryDecideAsync(rid, AdjustmentStatus.Approved, approver, decisionNote, now, ct)))
                {
                    throw new NotPendingException();
                }

                var before = license.Remaining;
                license.AdjustCredits(credits);
                await _unitOfWork.SaveChangesAsync(ct);
                var ledgerReason = requestId is null ? reason : $"{reason} (approved adjustment {requestId:N})";
                var entry = await _writer.AppendAsync(license, LedgerEntryType.Adjustment, credits, before, ct, reason: ledgerReason);
                _audit.Record(new AuditEntry("license.adjusted", nameof(License), license.Id.ToString(), license.ClientId,
                    NewValues: new { Credits = credits, Reason = reason, RequestId = requestId, RequestedBy = requestedBy }));
                if (requestId is { } id)
                {
                    await _unitOfWork.SaveChangesAsync(ct); // assigns the ledger row id
                    await _adjustmentAtomics.SetLedgerEntryAsync(id, entry.Id, ct);
                    _audit.Record(new AuditEntry("license.adjustment_approved", nameof(LicenseAdjustmentRequest), id.ToString(), license.ClientId,
                        NewValues: new { LicenseId = license.Id, Credits = credits, RequestedBy = requestedBy, LedgerTransactionId = entry.Id, Note = decisionNote }));
                }
            }, cancellationToken);
        }
        catch (NotPendingException)
        {
            _unitOfWork.ClearTracked();
            return NotPending();
        }
    }

    /// <summary>If the request is past its deadline, records the expiry (once, with an audit row) and returns the 409 to give back.</summary>
    private async Task<Error?> ExpiredResultAsync(LicenseAdjustmentRequest pending, DateTime now, CancellationToken cancellationToken)
    {
        if (pending.Status == AdjustmentStatus.Expired || (pending.Status == AdjustmentStatus.Pending && pending.ExpiresAt <= now))
        {
            if (pending.Status == AdjustmentStatus.Pending && await _adjustmentAtomics.TryExpireAsync(pending.Id, now, cancellationToken))
            {
                _audit.Record(new AuditEntry("license.adjustment_expired", nameof(LicenseAdjustmentRequest), pending.Id.ToString(), pending.ClientId,
                    NewValues: new { pending.LicenseId, pending.Credits, pending.RequestedBy }));
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return Error.Conflict(ErrorCodes.ApprovalExpired, "This adjustment request has expired. Ask for it again.");
        }

        return pending.Status == AdjustmentStatus.Pending ? null : NotPending();
    }

    private static Error NotPending() => Error.Conflict(ErrorCodes.ApprovalNotPending, "This adjustment request has already been decided.");

    private static AdjustmentRequestDto ToDto(LicenseAdjustmentRequest r, DateTime now) => new(
        r.Id, r.LicenseId, r.ClientId, r.Credits, r.Reason, r.RequestedBy, r.RequestedAt, r.ExpiresAt, r.EffectiveStatus(now).ToString(),
        r.DecidedBy, r.DecidedAt, r.DecisionNote, r.LedgerTransactionId);

    private sealed class NotPendingException : Exception
    {
    }
}
