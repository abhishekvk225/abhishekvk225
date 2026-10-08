using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Common;
using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.Licensing;

/// <summary>What a pre-flight learned: this operation costs <see cref="Cost"/> credits under <see cref="Policy"/>.</summary>
public sealed record MeterTicket(MeteredOperation Operation, int Cost, ChargePolicy Policy, int AvailableCredits);

public sealed record ChargeCommand(
    Guid ClientId,
    MeteredOperation Operation,
    MeterOutcome Outcome,
    Guid? RecognitionRequestId,
    string? IdempotencyKey);

/// <summary>Outcome of a charge. <see cref="Charged"/> is 0 when the operation was free, not billable under its policy, or a replay.</summary>
public sealed record ChargeResult(
    int Charged,
    int? RemainingBalance,
    Guid? LicenseId,
    long? TransactionId,
    bool Replayed);

public interface ILicenseMeteringService
{
    /// <summary>
    /// Cheap gate to run BEFORE any image processing: is there a usable license with enough credits? Does not deduct.
    /// Failure codes: LICENSE_NOT_FOUND, LICENSE_EXPIRED, LICENSE_SUSPENDED, LICENSE_INSUFFICIENT_BALANCE.
    /// </summary>
    Task<Result<MeterTicket>> PreflightAsync(Guid clientId, MeteredOperation operation, CancellationToken cancellationToken);

    /// <summary>
    /// Deducts the operation's cost (if billable for this outcome) atomically and records it in the immutable ledger — in one
    /// transaction. Replaying the same idempotency key never charges twice.
    /// </summary>
    Task<Result<ChargeResult>> ChargeAsync(ChargeCommand command, CancellationToken cancellationToken);
}

public sealed class LicenseMeteringService : ILicenseMeteringService
{
    private readonly IMeteringStore _store;
    private readonly ICostRuleResolver _costs;
    private readonly ILedgerRepository _ledger;
    private readonly LedgerWriter _writer;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;

    public LicenseMeteringService(
        IMeteringStore store,
        ICostRuleResolver costs,
        ILedgerRepository ledger,
        LedgerWriter writer,
        IUnitOfWork unitOfWork,
        TimeProvider time)
    {
        _store = store;
        _costs = costs;
        _ledger = ledger;
        _writer = writer;
        _unitOfWork = unitOfWork;
        _time = time;
    }

    public async Task<Result<MeterTicket>> PreflightAsync(Guid clientId, MeteredOperation operation, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var usable = await _store.GetUsableAsync(clientId, now, cancellationToken);

        // The ticket reports the cost under the license that would be charged first (earliest expiring that can afford it).
        foreach (var license in usable)
        {
            var cost = await _costs.ResolveAsync(clientId, license.PlanId, operation, now, cancellationToken);
            if (license.Remaining >= cost.Credits)
            {
                return new MeterTicket(operation, cost.Credits, cost.Policy, usable.Sum(l => l.Remaining));
            }
        }

        // Free operations (cost 0) still need an active license so a suspended/expired client cannot use the service at all.
        if (usable.Count > 0)
        {
            var cost = await _costs.ResolveAsync(clientId, usable[0].PlanId, operation, now, cancellationToken);
            if (cost.Credits == 0)
            {
                return new MeterTicket(operation, 0, cost.Policy, usable.Sum(l => l.Remaining));
            }

            return Error.PaymentRequired(ErrorCodes.LicenseInsufficientBalance, "Not enough credits to perform this operation.");
        }

        return await ExplainUnavailableAsync(clientId, now, cancellationToken);
    }

    public async Task<Result<ChargeResult>> ChargeAsync(ChargeCommand command, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        if (command.IdempotencyKey is { } key && await _ledger.FindByIdempotencyKeyAsync(command.ClientId, key, cancellationToken) is { } existing)
        {
            return Replay(existing);
        }

        try
        {
            return await _unitOfWork.ExecuteInTransactionAsync(ct => ChargeInsideTransactionAsync(command, now, ct), cancellationToken);
        }
        catch (UniqueConstraintViolationException) when (command.IdempotencyKey is not null)
        {
            // Two requests with the same key raced: the loser's deduction was rolled back with its transaction; return the winner's.
            var winner = await _ledger.FindByIdempotencyKeyAsync(command.ClientId, command.IdempotencyKey, cancellationToken);
            return winner is null ? Error.Conflict(ErrorCodes.Conflict, "A request with this idempotency key is in progress.") : Replay(winner);
        }
    }

    private async Task<Result<ChargeResult>> ChargeInsideTransactionAsync(ChargeCommand command, DateTime now, CancellationToken ct)
    {
        var usable = await _store.GetUsableAsync(command.ClientId, now, ct);
        var anyBillable = false;

        // Earliest-expiring first (FEFO). A charge is never split across licenses.
        foreach (var license in usable)
        {
            var cost = await _costs.ResolveAsync(command.ClientId, license.PlanId, command.Operation, now, ct);
            if (cost.Credits == 0 || !cost.Policy.ShouldCharge(command.Outcome))
            {
                return new ChargeResult(0, null, null, null, false); // free or not billable for this outcome
            }

            anyBillable = true;
            if (license.Remaining < cost.Credits)
            {
                continue;
            }

            var consumed = await _store.TryConsumeAsync(license.Id, command.ClientId, cost.Credits, now, ct);
            if (consumed is null)
            {
                continue; // lost a race for this license's last credits: try the next one
            }

            var entry = await _writer.AppendAsync(
                license, LedgerEntryType.Consume, -cost.Credits, consumed.BalanceBefore, ct,
                operation: command.Operation, recognitionRequestId: command.RecognitionRequestId, idempotencyKey: command.IdempotencyKey);
            await _unitOfWork.SaveChangesAsync(ct);
            return new ChargeResult(cost.Credits, consumed.BalanceAfter, license.Id, entry.Id, false);
        }

        if (usable.Count > 0 && !anyBillable)
        {
            return new ChargeResult(0, null, null, null, false);
        }

        return usable.Count > 0
            ? Error.PaymentRequired(ErrorCodes.LicenseInsufficientBalance, "Not enough credits to complete this operation.")
            : await ExplainUnavailableAsync(command.ClientId, now, ct);
    }

    private static ChargeResult Replay(LicenseTransaction existing) =>
        new(-existing.Credits, existing.BalanceAfter, existing.LicenseId, existing.Id, true);

    /// <summary>Picks the most useful reason why no license could be used (balance &gt; suspended &gt; expired &gt; none).</summary>
    private async Task<Error> ExplainUnavailableAsync(Guid clientId, DateTime now, CancellationToken cancellationToken)
    {
        var all = await _store.GetAvailabilityAsync(clientId, now, cancellationToken);

        if (all.Any(a => a.Status == LicenseStatus.Active && a.InPeriod && a.Remaining <= 0))
        {
            return Error.PaymentRequired(ErrorCodes.LicenseInsufficientBalance, "Your credits are used up. Please renew or top up your license.");
        }

        if (all.Any(a => a.Status == LicenseStatus.Suspended && !a.Ended))
        {
            return Error.Forbidden(ErrorCodes.LicenseSuspended, "Your license is suspended. Please contact support.");
        }

        if (all.Any(a => a.Ended || a.Status == LicenseStatus.Expired))
        {
            return Error.PaymentRequired(ErrorCodes.LicenseExpired, "Your license has expired. Please renew it.");
        }

        return Error.PaymentRequired(ErrorCodes.LicenseNotFound, "There is no active license for this account.");
    }
}
