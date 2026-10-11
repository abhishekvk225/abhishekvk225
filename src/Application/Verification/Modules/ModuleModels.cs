using NexaVerify.Application.Common;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Modules;

public enum ModuleStatus
{
    Available = 0,
    Upcoming,
    Disabled,
}

/// <summary>
/// A catalogue entry. Upcoming entries are DATA ONLY: <c>Type</c> null and empty permissions, operations and providers, so they have no
/// route, no processor, no permission key and no <c>MeteredOperation</c>, and can never be charged or submitted.
/// </summary>
public sealed record VerificationModuleDescriptor(
    string Key,
    string DisplayName,
    string Description,
    ModuleStatus Status,
    int DisplayOrder,
    VerificationType? Type,
    IReadOnlyList<string> SubmitPermissions,
    IReadOnlyList<MeteredOperation> CostOperations,
    IReadOnlyList<string> ProviderIds,
    string? PlannedNote);

public interface IVerificationModule
{
    VerificationModuleDescriptor Descriptor { get; }
}

public interface IVerificationModuleRegistry
{
    /// <summary>Code-defined, ordered by <c>DisplayOrder</c>.</summary>
    IReadOnlyList<VerificationModuleDescriptor> All { get; }

    VerificationModuleDescriptor? Find(string key);

    VerificationModuleDescriptor? Find(VerificationType type);

    /// <summary>
    /// Upcoming stays Upcoming; Available becomes Disabled when the platform switch is off. Unknown keys report Disabled and never
    /// pass the gate. An Available module without a settings row is treated as enabled (the switch only turns modules off).
    /// </summary>
    Task<ModuleStatus> GetEffectiveStatusAsync(string key, CancellationToken cancellationToken);
}

public interface IModuleGate
{
    /// <summary>Failure = 404 <c>VERIFICATION_MODULE_DISABLED</c> (hidden, like BILLING_DISABLED). Never succeeds for Upcoming or unknown modules.</summary>
    Task<Result<VerificationModuleDescriptor>> EnsureAvailableAsync(VerificationType type, CancellationToken cancellationToken);
}

/// <summary>Read side of <c>verification.ModuleSettings</c> (implemented by the persistence layer).</summary>
public interface IVerificationModuleSettingReader
{
    /// <summary>Null when the module has no row.</summary>
    Task<bool?> IsEnabledAsync(string moduleKey, CancellationToken cancellationToken);
}
