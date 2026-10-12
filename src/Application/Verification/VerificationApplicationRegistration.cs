using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Admin;
using NexaVerify.Application.Verification.Billing;
using NexaVerify.Application.Verification.Modules;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Application.Verification.Providers;
using NexaVerify.Application.Verification.Query;
using NexaVerify.Application.Verification.Submission;

namespace NexaVerify.Application.Verification;

public static class VerificationApplicationRegistration
{
    /// <summary>
    /// Registers the module catalogue (Available and Upcoming descriptors as data), the registry, the module gate, the options validator
    /// and the application services (submission, processing, query, lifecycle, downloads, admin, billing facade). The host binds
    /// <see cref="VerificationOptions"/> and calls <c>ValidateOnStart</c> (see <c>AddVerificationInfrastructure</c>); document handling,
    /// providers, storage and persistence are Infrastructure's.
    /// </summary>
    public static IServiceCollection AddVerificationApplication(this IServiceCollection services)
    {
        foreach (var descriptor in ModuleCatalogue.All)
        {
            services.AddSingleton<IVerificationModule>(new CatalogueModule(descriptor));
        }

        services.TryAddScoped<IVerificationModuleRegistry, VerificationModuleRegistry>();
        services.TryAddScoped<IModuleGate, ModuleGate>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<VerificationOptions>, VerificationOptionsValidator>());

        services.TryAddSingleton<IVerificationProviderCatalog, VerificationProviderCatalog>();
        services.TryAddScoped<VerificationEnvelopeCodec>();
        services.TryAddScoped<IVerificationMeter, VerificationMeter>();
        services.TryAddScoped<VerificationSubmissionCore>();
        services.TryAddScoped<IBankStatementVerificationService, BankStatementVerificationService>();
        services.TryAddScoped<IPanVerificationService, PanVerificationService>();
        services.TryAddScoped<IPassportVerificationService, PassportVerificationService>();
        services.TryAddScoped<IVerificationProcessor, VerificationProcessor>();
        services.TryAddScoped<IVerificationQueryService, VerificationQueryService>();
        services.TryAddScoped<IVerificationLifecycleService, VerificationLifecycleService>();
        services.TryAddScoped<IVerificationDownloadService, VerificationDownloadService>();
        services.TryAddScoped<IVerificationAdminService, VerificationAdminService>();
        return services;
    }
}
