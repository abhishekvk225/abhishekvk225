using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Modules;
using NexaVerify.Application.Verification.Processing;

namespace NexaVerify.Application.Verification;

public static class VerificationApplicationRegistration
{
    /// <summary>
    /// Registers the module catalogue (Available and Upcoming descriptors as data), the registry, the module gate and the options
    /// validator. Behaviour (submission core, processor, meter, query) is registered by later steps; the host binds
    /// <see cref="VerificationOptions"/> and calls <c>ValidateOnStart</c> (see <c>AddVerificationInfrastructure</c>).
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
        return services;
    }
}
