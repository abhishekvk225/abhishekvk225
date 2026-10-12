using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Modules;
using NexaVerify.Infrastructure.Persistence.Queries;
using NexaVerify.Infrastructure.Persistence.Repositories;
using NexaVerify.Infrastructure.Platform;

namespace NexaVerify.Infrastructure.Persistence;

public static class VerificationPersistenceRegistration
{
    /// <summary>Registers the M14 persistence ports (all scoped: they share the request's/job's <see cref="AppDbContext"/> and tenant scope).</summary>
    public static IServiceCollection AddVerificationPersistence(this IServiceCollection services)
    {
        services.AddScoped<IVerificationRepository, VerificationRepository>();
        services.AddScoped<IVerificationDocumentStore, VerificationDocumentStore>();
        services.AddScoped<IVerificationResultStore, VerificationResultStore>();
        services.AddScoped<IVerificationQueue, VerificationQueue>();
        services.AddScoped<IVerificationDownloadGrants, VerificationDownloadGrantStore>();
        services.AddScoped<IVerificationRetention, VerificationRetention>();
        services.AddScoped<IVerificationAdminQueries, VerificationAdminQueries>();
        services.AddScoped<VerificationModuleSettingStore>();
        services.AddScoped<IVerificationModuleSettings>(sp => sp.GetRequiredService<VerificationModuleSettingStore>());
        services.AddScoped<IVerificationModuleSettingReader>(sp => sp.GetRequiredService<VerificationModuleSettingStore>());
        return services;
    }
}
