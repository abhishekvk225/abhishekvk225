using NexaVerify.Application.Common;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Modules;

public sealed class VerificationModuleRegistry : IVerificationModuleRegistry
{
    private readonly IReadOnlyList<VerificationModuleDescriptor> _all;
    private readonly Dictionary<string, VerificationModuleDescriptor> _byKey;
    private readonly Dictionary<VerificationType, VerificationModuleDescriptor> _byType;
    private readonly IVerificationModuleSettingReader _settings;

    /// <summary>Fails fast (start-up) on duplicate keys or on a descriptor that breaks the Upcoming/Available invariants.</summary>
    public VerificationModuleRegistry(IEnumerable<IVerificationModule> modules, IVerificationModuleSettingReader settings)
    {
        _settings = settings;
        var descriptors = modules.Select(m => m.Descriptor).OrderBy(d => d.DisplayOrder).ThenBy(d => d.Key, StringComparer.Ordinal).ToList();
        _all = descriptors;
        _byKey = new Dictionary<string, VerificationModuleDescriptor>(StringComparer.OrdinalIgnoreCase);
        _byType = [];

        foreach (var d in descriptors)
        {
            Validate(d);
            if (!_byKey.TryAdd(d.Key, d))
            {
                throw new InvalidOperationException($"Duplicate verification module key '{d.Key}'.");
            }

            if (d.Type is { } type && !_byType.TryAdd(type, d))
            {
                throw new InvalidOperationException($"More than one verification module is registered for type {type}.");
            }
        }
    }

    public IReadOnlyList<VerificationModuleDescriptor> All => _all;

    public VerificationModuleDescriptor? Find(string key) => _byKey.GetValueOrDefault(key ?? string.Empty);

    public VerificationModuleDescriptor? Find(VerificationType type) => _byType.GetValueOrDefault(type);

    public async Task<ModuleStatus> GetEffectiveStatusAsync(string key, CancellationToken cancellationToken)
    {
        var descriptor = Find(key);
        if (descriptor is null)
        {
            return ModuleStatus.Disabled;
        }

        if (descriptor.Status != ModuleStatus.Available)
        {
            return descriptor.Status == ModuleStatus.Upcoming ? ModuleStatus.Upcoming : ModuleStatus.Disabled;
        }

        return await _settings.IsEnabledAsync(descriptor.Key, cancellationToken) is false ? ModuleStatus.Disabled : ModuleStatus.Available;
    }

    /// <summary>The catalogue invariants that make "Upcoming cannot bill, submit or have endpoints" structural.</summary>
    internal static void Validate(VerificationModuleDescriptor d)
    {
        if (string.IsNullOrWhiteSpace(d.Key))
        {
            throw new InvalidOperationException("A verification module needs a key.");
        }

        switch (d.Status)
        {
            case ModuleStatus.Upcoming when d.Type is not null || d.SubmitPermissions.Count > 0 || d.CostOperations.Count > 0 || d.ProviderIds.Count > 0:
                throw new InvalidOperationException($"Upcoming module '{d.Key}' must have no type, permissions, cost operations or providers.");
            case ModuleStatus.Available when d.Type is null || d.SubmitPermissions.Count == 0 || d.CostOperations.Count == 0:
                throw new InvalidOperationException($"Available module '{d.Key}' needs a type, a submit permission and at least one cost operation.");
            case ModuleStatus.Disabled:
                throw new InvalidOperationException($"Module '{d.Key}': Disabled is a computed status, not a catalogue status.");
        }
    }
}

public sealed class ModuleGate : IModuleGate
{
    private readonly IVerificationModuleRegistry _registry;

    public ModuleGate(IVerificationModuleRegistry registry)
    {
        _registry = registry;
    }

    public async Task<Result<VerificationModuleDescriptor>> EnsureAvailableAsync(VerificationType type, CancellationToken cancellationToken)
    {
        var descriptor = _registry.Find(type);
        if (descriptor is null || await _registry.GetEffectiveStatusAsync(descriptor.Key, cancellationToken) != ModuleStatus.Available)
        {
            return new Error(ErrorCodes.VerificationModuleDisabled, "This verification module is not available.", ErrorType.NotFound);
        }

        return descriptor;
    }
}
