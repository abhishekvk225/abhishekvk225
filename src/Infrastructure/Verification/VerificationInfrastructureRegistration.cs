using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Application.Verification.Processing;

namespace NexaVerify.Infrastructure.Verification;

public static class VerificationInfrastructureRegistration
{
    /// <summary>
    /// Binds <c>Verification:*</c> and validates it at start-up (<see cref="VerificationOptionsValidator"/> is registered by
    /// <c>AddVerificationApplication</c>). Storage, worker, providers and persistence adapters are added by later steps in this file.
    /// </summary>
    public static IServiceCollection AddVerificationInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<VerificationOptions>()
            .Bind(configuration.GetSection(VerificationOptions.SectionName))
            .ValidateOnStart();
        return services;
    }
}
