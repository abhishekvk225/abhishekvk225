using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.BankStatements;
using NexaVerify.Application.Verification.Matching;
using NexaVerify.Application.Verification.Pan;
using NexaVerify.Application.Verification.Passport;
using NexaVerify.Application.Verification.Processing;

namespace NexaVerify.Application.Verification;

public static class VerificationModuleLogicRegistration
{
    /// <summary>
    /// Pure module logic and the three <see cref="IModuleProcessor"/>s (Backend-B). Needs <c>IDocumentParser</c> and the provider resolvers
    /// (registered by <c>AddVerificationProcessing</c> in Infrastructure, which calls this). Does NOT register any <c>IVerificationModule</c>.
    /// </summary>
    public static IServiceCollection AddVerificationModuleLogic(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<INameMatcher, NameMatcher>();
        services.TryAddSingleton<IBankRuleSetProvider, BankRuleSetProvider>();
        services.TryAddSingleton<IBankStatementParser, BankStatementParser>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<VerificationOptions>, BankRuleSetsOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleProcessor, BankStatementModuleProcessor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleProcessor, PanModuleProcessor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleProcessor, PassportModuleProcessor>());
        return services;
    }
}
