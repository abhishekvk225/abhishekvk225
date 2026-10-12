using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Infrastructure.Background;
using NexaVerify.Infrastructure.Verification.Storage;

namespace NexaVerify.Infrastructure.Verification;

public static class VerificationInfrastructureRegistration
{
    /// <summary>
    /// Binds <c>Verification:*</c> and validates it at start-up (<see cref="VerificationOptionsValidator"/> is registered by
    /// <c>AddVerificationApplication</c>), then adds document processing (inspector, parsers, OCR, not-configured providers, module
    /// processors), the encrypted document storage, the <see cref="VerificationWorker"/> and the <see cref="VerificationRetentionSweeper"/>.
    /// Persistence ports are added by <c>AddVerificationPersistence</c>. There is deliberately no vendor adapter and no simulated provider here.
    /// </summary>
    public static IServiceCollection AddVerificationInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<VerificationOptions>()
            .Bind(configuration.GetSection(VerificationOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<VerificationRetentionJobOptions>()
            .Bind(configuration.GetSection(VerificationRetentionJobOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddVerificationProcessing(configuration);
        services.TryAddScoped<IDocumentStorageService, SqlDocumentStorageService>();

        services.AddSingleton<VerificationWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<VerificationWorker>());
        services.AddSingleton<VerificationRetentionSweeper>();
        services.AddHostedService(sp => sp.GetRequiredService<VerificationRetentionSweeper>());
        return services;
    }
}
