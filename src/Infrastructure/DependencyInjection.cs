using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Identity;
using NexaVerify.Application.Persistence;
using NexaVerify.Application.Auditing;
using NexaVerify.Infrastructure.Auditing;
using NexaVerify.Infrastructure.Identity;
using NexaVerify.Infrastructure.Messaging;
using NexaVerify.Infrastructure.Persistence.Guards;
using NexaVerify.Infrastructure.Persistence.Maintenance;
using NexaVerify.Infrastructure.Persistence.Repositories;
using NexaVerify.Infrastructure.Persistence.Seed;
using NexaVerify.Infrastructure.Security;
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
        services.AddScoped<TenantSessionContextApplier>();
        services.AddScoped<TenantSessionContextInterceptor>();
        services.AddScoped<TenantSessionContextCommandInterceptor>();

        // Plain AddDbContext only: the interceptors are scoped to the request/job and must not be captured by a pool or factory.
        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            var db = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlServer(connectionString, sql => sql.CommandTimeout(db.CommandTimeoutSeconds));
            options.AddInterceptors(
                sp.GetRequiredService<AuditAndTenantSaveChangesInterceptor>(),
                sp.GetRequiredService<TenantSessionContextInterceptor>(),
                sp.GetRequiredService<TenantSessionContextCommandInterceptor>());
        });

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<RowLevelSecurityInstaller>();
        services.AddScoped<DatabaseGuardsInstaller>();
        services.AddScoped<DatabaseInitializer>();
        services.AddScoped<IdentitySeeder>();

        // Options (validated at startup)
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<PasswordPolicyOptions>().Bind(configuration.GetSection(PasswordPolicyOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<PasswordHashingOptions>().Bind(configuration.GetSection(PasswordHashingOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection(EmailOptions.SectionName));
        services.AddOptions<SeedOptions>().Bind(configuration.GetSection(SeedOptions.SectionName));

        // Repositories and identity services
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IPasswordResetTokenRepository, PasswordResetTokenRepository>();
        services.AddScoped<ILoginHistoryRepository, LoginHistoryRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IAuditLogWriter, AuditLogWriter>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IPermissionResolver, PermissionResolver>();
        services.AddScoped<ISessionValidator, SessionValidator>();
        services.AddMemoryCache();

        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ISecureTokenService, SecureTokenService>();
        services.AddSingleton<JwtKeyProvider>();
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.TryAddSingleton<IEmailSender, LoggingEmailSender>();
        services.TryAddScoped<IClientAccessGuard, AllowAllClientAccessGuard>();
        services.TryAddScoped<IRequestInfo, NullRequestInfo>();

        return services;
    }
}
