using NexaVerify.Domain.Licensing;

namespace NexaVerify.Domain.Verification;

/// <summary>Maps a verification type and billable stage to its <see cref="MeteredOperation"/>, and holds the default platform prices (docs/07 §8.2).</summary>
public static class VerificationOperations
{
    /// <summary>Null when the type has no such stage (bank statements have no provider stage in release 1).</summary>
    public static MeteredOperation? For(VerificationType type, VerificationStage stage) => (type, stage) switch
    {
        (VerificationType.BankStatement, VerificationStage.Extraction) => MeteredOperation.BankStatementExtraction,
        (VerificationType.Pan, VerificationStage.Extraction) => MeteredOperation.PanExtraction,
        (VerificationType.Pan, VerificationStage.Provider) => MeteredOperation.PanProviderCheck,
        (VerificationType.Passport, VerificationStage.Extraction) => MeteredOperation.PassportExtraction,
        (VerificationType.Passport, VerificationStage.Provider) => MeteredOperation.PassportProviderCheck,
        _ => null,
    };

    public static bool IsVerificationOperation(MeteredOperation operation) => operation >= MeteredOperation.BankStatementExtraction;

    /// <summary>
    /// Proposed platform-default cost rules (policy <c>OnCompleted</c>), to be seeded by the persistence layer. The built-in resolver
    /// fallback of 1 credit must never silently price these operations.
    /// </summary>
    public static IReadOnlyDictionary<MeteredOperation, int> DefaultCredits { get; } = new Dictionary<MeteredOperation, int>
    {
        [MeteredOperation.BankStatementExtraction] = 5,
        [MeteredOperation.PanExtraction] = 1,
        [MeteredOperation.PanProviderCheck] = 2,
        [MeteredOperation.PassportExtraction] = 2,
        [MeteredOperation.PassportProviderCheck] = 3,
    };

    public const ChargePolicy DefaultPolicy = ChargePolicy.OnCompleted;
}
