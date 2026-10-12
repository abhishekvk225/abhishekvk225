using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Infrastructure.Verification.Providers;

/// <summary>
/// Release 1 ships no authoritative PAN provider: no vendor is named and no contract or credentials exist, so an adapter cannot be built
/// or verified honestly. This implementation always answers <c>ProviderNotConfigured</c>; it can never produce a verified result.
/// A real adapter implements <see cref="IPanVerificationProvider"/> (typed HttpClient, https-only host from configuration, no redirects,
/// timeout from <c>Verification:Providers:Pan:TimeoutSeconds</c>, secrets from configuration, every failure mapped to
/// Timeout/ProviderError) and is registered under its own id; <see cref="PanVerificationProviderResolver"/> then selects it.
/// </summary>
public sealed class NotConfiguredPanVerificationProvider : IPanVerificationProvider
{
    public string ProviderId => ProviderOptions.None;

    public bool IsConfigured => false;

    public Task<ProviderResult> VerifyAsync(PanProviderRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderResult(ExternalState.ProviderNotConfigured, null, [], "PROVIDER_NOT_CONFIGURED"));
}

/// <summary>See <see cref="NotConfiguredPanVerificationProvider"/>; the same applies to passports.</summary>
public sealed class NotConfiguredPassportVerificationProvider : IPassportVerificationProvider
{
    public string ProviderId => ProviderOptions.None;

    public bool IsConfigured => false;

    public Task<ProviderResult> VerifyAsync(PassportProviderRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderResult(ExternalState.ProviderNotConfigured, null, [], "PROVIDER_NOT_CONFIGURED"));
}

/// <summary>
/// Picks the provider named by <c>Verification:Providers:Pan:Provider</c> among the registered ones. <c>None</c>, an unknown id or a
/// provider that reports <c>IsConfigured = false</c> all resolve to the not-configured provider: the fallback is never a "success".
/// </summary>
public sealed class PanVerificationProviderResolver : IPanVerificationProviderResolver
{
    private readonly IPanVerificationProvider _selected;

    public PanVerificationProviderResolver(
        IOptions<VerificationOptions> options, IEnumerable<IPanVerificationProvider> providers, ILogger<PanVerificationProviderResolver> logger)
    {
        var name = options.Value.Providers.Pan.Provider;
        var list = providers.ToList();
        var match = string.Equals(name, ProviderOptions.None, StringComparison.OrdinalIgnoreCase)
            ? null
            : list.FirstOrDefault(p => p.IsConfigured && string.Equals(p.ProviderId, name, StringComparison.OrdinalIgnoreCase));
        if (match is null && !string.Equals(name, ProviderOptions.None, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("PAN provider is set but no configured provider with that id is registered; falling back to not configured");
        }

        _selected = match ?? list.OfType<NotConfiguredPanVerificationProvider>().FirstOrDefault() ?? new NotConfiguredPanVerificationProvider();
    }

    public IPanVerificationProvider Resolve() => _selected;
}

public sealed class PassportVerificationProviderResolver : IPassportVerificationProviderResolver
{
    private readonly IPassportVerificationProvider _selected;

    public PassportVerificationProviderResolver(
        IOptions<VerificationOptions> options, IEnumerable<IPassportVerificationProvider> providers, ILogger<PassportVerificationProviderResolver> logger)
    {
        var name = options.Value.Providers.Passport.Provider;
        var list = providers.ToList();
        var match = string.Equals(name, ProviderOptions.None, StringComparison.OrdinalIgnoreCase)
            ? null
            : list.FirstOrDefault(p => p.IsConfigured && string.Equals(p.ProviderId, name, StringComparison.OrdinalIgnoreCase));
        if (match is null && !string.Equals(name, ProviderOptions.None, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Passport provider is set but no configured provider with that id is registered; falling back to not configured");
        }

        _selected = match ?? list.OfType<NotConfiguredPassportVerificationProvider>().FirstOrDefault() ?? new NotConfiguredPassportVerificationProvider();
    }

    public IPassportVerificationProvider Resolve() => _selected;
}
