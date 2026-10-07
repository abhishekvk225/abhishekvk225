using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexaVerify.Application.Abstractions;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.Infrastructure.Persistence.Interceptors;
using NexaVerify.Infrastructure.Persistence.Rls;
using NexaVerify.Infrastructure.Tenancy;

namespace NexaVerify.Infrastructure;

public static class DependencyInjection
{
    public const string DefaultConnectionName = "Default";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(DefaultConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string 'ConnectionStrings:{DefaultConnectionName}' is not configured. " +
                "Provide it through user-secrets (development) or environment variables / a secret store (production).");
        }

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ICurrentUser, AnonymousCurrentUser>();

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ITenantScope>(sp => sp.GetRequiredService<TenantContext>());

        services.AddScoped<AuditAndTenantSaveChangesInterceptor>();
        services.AddScoped<TenantSessionContextInterceptor>();

        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            var db = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseOptions>>().Value;
            options.UseSqlServer(connectionString, sql => sql.CommandTimeout(db.CommandTimeoutSeconds));
            options.AddInterceptors(
                sp.GetRequiredService<AuditAndTenantSaveChangesInterceptor>(),
                sp.GetRequiredService<TenantSessionContextInterceptor>());
        });

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<RowLevelSecurityInstaller>();

        return services;
    }
}
