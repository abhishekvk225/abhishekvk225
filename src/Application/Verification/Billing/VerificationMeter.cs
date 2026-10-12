using NexaVerify.Application.Common;
using NexaVerify.Application.Licensing;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Billing;

/// <summary>
/// Thin facade over the existing metering service: derives the <see cref="MeteredOperation"/> from (type, stage), builds the stable ledger
/// idempotency key and nothing else. There is no balance, hold or counter of its own: every credit moves through the ledger.
/// </summary>
public sealed class VerificationMeter : IVerificationMeter
{
    private readonly ILicenseMeteringService _metering;

    public VerificationMeter(ILicenseMeteringService metering)
    {
        _metering = metering;
    }

    public async Task<Result<VerificationMeterTicket>> PreflightAsync(Guid clientId, VerificationType type, bool includeProvider, CancellationToken cancellationToken)
    {
        var extraction = await PreflightStageAsync(clientId, type, VerificationStage.Extraction, cancellationToken).ConfigureAwait(false);
        if (extraction.IsFailure || !includeProvider || VerificationOperations.For(type, VerificationStage.Provider) is null)
        {
            return extraction;
        }

        var provider = await PreflightStageAsync(clientId, type, VerificationStage.Provider, cancellationToken).ConfigureAwait(false);
        if (provider.IsFailure)
        {
            return provider;
        }

        var available = Math.Min(extraction.Value.AvailableCredits, provider.Value.AvailableCredits);
        var needed = (long)extraction.Value.ExtractionCost + provider.Value.ExtractionCost;
        return available < needed
            ? Error.PaymentRequired(ErrorCodes.LicenseInsufficientBalance, "Not enough credits to complete this verification.")
            : new VerificationMeterTicket(extraction.Value.ExtractionCost, provider.Value.ExtractionCost, available);
    }

    public async Task<Result<VerificationMeterTicket>> PreflightStageAsync(Guid clientId, VerificationType type, VerificationStage stage, CancellationToken cancellationToken)
    {
        if (VerificationOperations.For(type, stage) is not { } operation)
        {
            return StageNotBillable();
        }

        var ticket = await _metering.PreflightAsync(clientId, operation, cancellationToken).ConfigureAwait(false);
        return ticket.IsFailure
            ? ticket.Error!
            : new VerificationMeterTicket(ticket.Value.Cost, 0, ticket.Value.AvailableCredits);
    }

    public async Task<Result<StageCharge>> ChargeStageAsync(
        Guid clientId, Guid requestId, VerificationType type, VerificationStage stage, MeterOutcome outcome, CancellationToken cancellationToken)
    {
        if (VerificationOperations.For(type, stage) is not { } operation)
        {
            return StageNotBillable();
        }

        var charge = await _metering.ChargeAsync(
            new ChargeCommand(clientId, operation, outcome, requestId, VerificationLedgerKeys.For(requestId, stage)), cancellationToken).ConfigureAwait(false);
        return charge.IsFailure
            ? charge.Error!
            : new StageCharge(charge.Value.Charged, charge.Value.RemainingBalance, charge.Value.TransactionId, charge.Value.Replayed);
    }

    private static Error StageNotBillable() =>
        Error.Failure("VERIFICATION_STAGE_NOT_BILLABLE", "This verification type has no such billable stage.");
}
