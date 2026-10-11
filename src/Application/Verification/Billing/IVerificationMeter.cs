using NexaVerify.Application.Common;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Billing;

/// <summary>What a pre-flight learned: the extraction stage (and the provider stage when included) cost this much and this many credits are usable.</summary>
public sealed record VerificationMeterTicket(int ExtractionCost, int ProviderCost, int AvailableCredits);

/// <summary>Outcome of charging one stage. <c>Charged</c> is 0 when the stage was free, not billable for this outcome, or a replay.</summary>
public sealed record StageCharge(int Charged, int? RemainingBalance, long? TransactionId, bool Replayed);

/// <summary>
/// The only M14 code that talks to <c>ILicenseMeteringService</c>. Stages are charged on completion, atomically with the stage result
/// (docs/07 §8). Ledger idempotency keys: <c>vr:{requestId:N}:x</c> (extraction), <c>vr:{requestId:N}:p</c> (provider). Fails for any
/// type/stage that has no <c>MeteredOperation</c> (Upcoming modules cannot be charged).
/// </summary>
public interface IVerificationMeter
{
    Task<Result<VerificationMeterTicket>> PreflightAsync(Guid clientId, VerificationType type, bool includeProvider, CancellationToken cancellationToken);

    Task<Result<StageCharge>> ChargeStageAsync(
        Guid clientId, Guid requestId, VerificationType type, VerificationStage stage, MeterOutcome outcome, CancellationToken cancellationToken);
}

public static class VerificationLedgerKeys
{
    public static string For(Guid requestId, VerificationStage stage) =>
        $"vr:{requestId:N}:{(stage == VerificationStage.Extraction ? 'x' : 'p')}";
}
