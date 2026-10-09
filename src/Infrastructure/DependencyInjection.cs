using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Identity;
using NexaVerify.Application.Persistence;
using NexaVerify.Application.Api;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Billing;
using NexaVerify.Infrastructure.Billing;
using NexaVerify.Application.Dashboards;
using NexaVerify.Application.Licensing;
using NexaVerify.Application.Public;
using NexaVerify.Application.Faces;
using NexaVerify.Infrastructure.Faces;
using NexaVerify.Infrastructure.Auditing;
using NexaVerify.Infrastructure.Background;
using NexaVerify.Infrastructure.Platform;
using NexaVerify.Infrastructure.Identity;
using NexaVerify.Infrastructure.Messaging;
using NexaVerify.Infrastructure.Persistence.Guards;
using NexaVerify.Infrastructure.Persistence.Maintenance;
using NexaVerify.Infrastructure.Persistence.Queries;
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
        services.AddOptions<LicensingOptions>().Bind(configuration.GetSection(LicensingOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<MfaOptions>().Bind(configuration.GetSection(MfaOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<PasswordPolicyOptions>().Bind(configuration.GetSection(PasswordPolicyOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<PasswordHashingOptions>().Bind(configuration.GetSection(PasswordHashingOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<SignupOptions>().Bind(configuration.GetSection(SignupOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<CaptchaOptions>().Bind(configuration.GetSection(CaptchaOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<PortalLinksOptions>().Bind(configuration.GetSection(PortalLinksOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<BillingOptions>().Bind(configuration.GetSection(BillingOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection(EmailOptions.SectionName));
        services.AddOptions<SeedOptions>().Bind(configuration.GetSection(SeedOptions.SectionName));

        // Repositories and identity services
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IPasswordResetTokenRepository, PasswordResetTokenRepository>();
        services.AddScoped<ILoginHistoryRepository, LoginHistoryRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IMfaRepository, MfaRepository>();
        services.AddScoped<IMfaAtomics, MfaAtomics>();
        services.AddScoped<IAuditLogWriter, AuditLogWriter>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ILoginThrottle, LoginThrottle>();
        services.AddScoped<IPendingSignupRepository, PendingSignupRepository>();
        services.AddScoped<IPendingSignupAtomics, PendingSignupAtomics>();
        services.AddScoped<IContactRequestRepository, ContactRequestRepository>();
        services.AddSingleton<IPublicThrottle, PublicThrottle>();
        services.AddHttpClient<ICaptchaVerifier, TurnstileCaptchaVerifier>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .ConfigureHttpClient((sp, client) => client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<CaptchaOptions>>().Value.TimeoutSeconds + 5));
        services.AddSingleton<PublicDataPurger>();
        services.AddHostedService(sp => sp.GetRequiredService<PublicDataPurger>());
        services.AddScoped<IRefreshTokenClaimer, RefreshTokenClaimer>();
        services.AddSingleton<NexaVerify.Application.Licensing.IDistributedLock, Platform.SqlDistributedLock>();
        services.AddSingleton<EmailOutbox>();
        services.TryAddSingleton<IEmailOutbox>(sp => sp.GetRequiredService<EmailOutbox>());
        services.AddHostedService<EmailDispatcher>();
        services.AddScoped<IPermissionResolver, PermissionResolver>();
        services.AddScoped<ISessionValidator, SessionValidator>();
        services.AddMemoryCache();

        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ISecureTokenService, SecureTokenService>();
        services.AddSingleton<JwtKeyProvider>();
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.TryAddSingleton<IEmailSender, LoggingEmailSender>();
        services.AddScoped<IClientAccessGuard, ClientAccessGuard>();
        services.AddScoped<IClientRepository, ClientRepository>();
        services.AddScoped<IClientSettingRepository, ClientSettingRepository>();
        services.AddScoped<IClientMembershipRepository, ClientMembershipRepository>();
        services.AddScoped<IClientQueries, ClientQueries>();
        services.AddOptions<EncryptionOptions>().Bind(configuration.GetSection(EncryptionOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<MasterKeyProvider>();
        services.AddSingleton<PlatformCrypto>();
        services.AddSingleton<IPlatformCrypto>(sp => sp.GetRequiredService<PlatformCrypto>());
        services.AddScoped<ClientKeyService>();
        services.AddScoped<IClientKeyProvisioner>(sp => sp.GetRequiredService<ClientKeyService>());
        services.AddScoped<IClientEncryption>(sp => sp.GetRequiredService<ClientKeyService>());
        services.TryAddScoped<IRequestInfo, NullRequestInfo>();

        services.AddScoped<ILicenseRepository, LicenseRepository>();
        services.AddScoped<ILedgerRepository, LedgerRepository>();
        services.AddScoped<IPlanRepository, PlanRepository>();
        services.AddScoped<ICostRuleRepository, CostRuleRepository>();
        services.AddScoped<IMeteringStore, MeteringStore>();
        services.AddScoped<ILicenseAdjustmentRepository, LicenseAdjustmentRepository>();
        services.AddScoped<ILicenseAdjustmentAtomics, LicenseAdjustmentAtomics>();
        services.AddScoped<LicensingSeeder>();

        // Online payments (M12). No card data ever reaches these services: the customer pays on the provider's hosted page.
        services.AddScoped<ICreditPackRepository, CreditPackRepository>();
        services.AddScoped<IPaymentOrderRepository, PaymentOrderRepository>();
        services.AddScoped<IPaymentEventRepository, PaymentEventRepository>();
        services.AddScoped<IBillingProfileRepository, BillingProfileRepository>();
        services.AddScoped<IPaymentOrderAtomics, PaymentOrderAtomics>();
        services.AddScoped<IInvoiceNumberAllocator, InvoiceNumberAllocator>();
        services.AddSingleton<IBillingThrottle, BillingThrottle>();
        services.AddHttpClient<StripeProvider>((sp, client) => ConfigureProviderClient(client, sp.GetRequiredService<IOptions<BillingOptions>>().Value.Stripe.ApiBaseUrl, sp.GetRequiredService<IOptions<BillingOptions>>().Value.Stripe.TimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient<RazorpayProvider>((sp, client) => ConfigureProviderClient(client, sp.GetRequiredService<IOptions<BillingOptions>>().Value.Razorpay.ApiBaseUrl, sp.GetRequiredService<IOptions<BillingOptions>>().Value.Razorpay.TimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<SimulatedProvider>();
        services.AddScoped<IPaymentProviderResolver, PaymentProviderResolver>();
        services.AddSingleton<BillingMaintenanceProcessor>();
        services.AddHostedService<BillingMaintenanceJob>();
        services.AddHostedService<LicenseExpirySweeper>();

        services.AddScoped<IFaceRepository, FaceRepository>();
        services.AddScoped<IRecognitionRepository, RecognitionRepository>();
        services.AddOptions<FaceEngineOptions>().Bind(configuration.GetSection(FaceEngineOptions.Section)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FaceEngineOptions>, FaceEngineOptionsValidator>());
        services.AddSingleton<IFaceEngine, MockFaceEngine>();
        services.AddSingleton<IImageProcessor, SkiaImageProcessor>();
        services.AddSingleton<TemplateIndexStore>();
        services.AddScoped<IEmbeddingCodec, EmbeddingCodec>();
        services.AddScoped<ITemplateIndex, TemplateIndex>();
        services.AddOptions<FaceRetentionOptions>().Bind(configuration.GetSection(FaceRetentionOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddHostedService<FaceRetentionSweeper>();

        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<IApiLogRepository, ApiLogRepository>();
        services.AddOptions<ApiAuthOptions>().Bind(configuration.GetSection(ApiAuthOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<ApiKeyNegativeCache>();
        services.AddScoped<ITransactionLock, SqlTransactionLock>();
        services.AddScoped<IApiKeyAuthenticator, ApiKeyAuthenticator>();
        services.AddScoped<IWebhookRepository, WebhookRepository>();
        services.AddOptions<WebhookOptions>().Bind(configuration.GetSection(WebhookOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => o.LeaseSeconds == 0 || o.LeaseSeconds >= o.DerivedLease.TotalSeconds, "Webhooks:LeaseSeconds is shorter than the worst case of one dispatch cycle (raise it, or leave it 0).")
            .ValidateOnStart();
        services.AddSingleton<IWebhookUrlGuard, WebhookUrlGuard>();
        services.AddScoped<IWebhookPublisher, WebhookPublisher>();
        services.AddScoped<WebhookStore>();
        services.AddSingleton<WebhookDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcher>());
        services.AddOptions<SharedCounterOptions>().Bind(configuration.GetSection(SharedCounterOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<ThrottleOptions>().Bind(configuration.GetSection(ThrottleOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton(new SharedCounterConnection(connectionString));
        services.AddSingleton<ICounterBackend, SqlCounterBackend>();
        services.AddSingleton<SharedWindowCounters>();
        services.AddSingleton<IApiUsageLimiter, ApiUsageLimiter>();
        services.AddSingleton<IPrincipalThrottle, PrincipalThrottle>();
        services.AddSingleton<UsageCounterPurger>();
        services.AddHostedService(sp => sp.GetRequiredService<UsageCounterPurger>());
        services.AddOptions<ApiLogOptions>().Bind(configuration.GetSection(ApiLogOptions.SectionName));
        services.AddSingleton<ApiRequestLogWriter>();
        services.AddSingleton<IApiRequestLogSink>(sp => sp.GetRequiredService<ApiRequestLogWriter>());
        services.AddHostedService(sp => sp.GetRequiredService<ApiRequestLogWriter>());

        services.AddOptions<DashboardOptions>().Bind(configuration.GetSection(DashboardOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddScoped<IDashboardQueries, DashboardQueries>();

        services.AddOptions<LedgerVerificationOptions>().Bind(configuration.GetSection(LedgerVerificationOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddScoped<ILedgerVerificationStore, LedgerVerificationStore>();
        services.AddScoped<LedgerAnchorService>();
        services.AddSingleton<ILedgerRunLauncher, LedgerRunLauncher>();
        services.AddHostedService<LedgerVerificationJob>();

        services.AddOptions<LicenseAlertOptions>().Bind(configuration.GetSection(LicenseAlertOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => o.ExpiringFinalNoticeDays < o.ExpiringNoticeDays, "Metering:Alerts: the final expiry notice must be shorter than the first.")
            .ValidateOnStart();
        services.AddScoped<ILicenseAlertRepository, LicenseAlertRepository>();
        services.AddScoped<ILicenseAlertCandidates, LicenseAlertCandidates>();
        services.AddSingleton<LicenseAlertProcessor>();
        services.AddHostedService<LicenseAlertJob>();

        return services;
    }

    private static void ConfigureProviderClient(HttpClient client, string baseUrl, int timeoutSeconds)
    {
        if (Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri))
        {
            client.BaseAddress = uri;
        }

        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
    }
}
