using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Modules;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Providers;

public sealed record ProviderStatus(string ProviderId, VerificationType Type, bool Configured);

/// <summary>What the platform can say about authoritative providers WITHOUT revealing configuration: ids and booleans only.</summary>
public interface IVerificationProviderCatalog
{
    /// <summary>Does this type have an authoritative provider stage at all? (Bank statements do not in release 1.)</summary>
    bool HasProviderStage(VerificationType type);

    /// <summary>True only when a real provider is selected, registered and reports itself configured.</summary>
    bool IsConfigured(VerificationType type);

    IReadOnlyList<ProviderStatus> Describe();
}

public sealed class VerificationProviderCatalog : IVerificationProviderCatalog
{
    private readonly IPanVerificationProviderResolver _pan;
    private readonly IPassportVerificationProviderResolver _passport;

    public VerificationProviderCatalog(IPanVerificationProviderResolver pan, IPassportVerificationProviderResolver passport)
    {
        _pan = pan;
        _passport = passport;
    }

    public bool HasProviderStage(VerificationType type) => type is VerificationType.Pan or VerificationType.Passport;

    public bool IsConfigured(VerificationType type) => type switch
    {
        VerificationType.Pan => _pan.Resolve().IsConfigured,
        VerificationType.Passport => _passport.Resolve().IsConfigured,
        _ => false,
    };

    public IReadOnlyList<ProviderStatus> Describe()
    {
        var pan = _pan.Resolve();
        var passport = _passport.Resolve();
        return [new ProviderStatus(pan.ProviderId, VerificationType.Pan, pan.IsConfigured), new ProviderStatus(passport.ProviderId, VerificationType.Passport, passport.IsConfigured)];
    }
}
