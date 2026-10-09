using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using NexaVerify.Web.Services;

namespace NexaVerify.Web.Security;

public static class PortalServiceExtensions
{
    /// <summary>Config key that turns on the design-review stub clients (Development, or UiDemo with <see cref="AllowDemoStubsKey"/>).</summary>
    public const string UseStubsKey = "Ui:UseStubClients";

    public const string AllowDemoStubsKey = "Ui:AllowDemoStubs";

    /// <summary>
    /// Registers the BFF: server-side sessions, the opaque HttpOnly cookie, policies, HTTP clients and the typed API clients.
    /// </summary>
    public static IServiceCollection AddPortalBff(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        PortalStartupGuards.Validate(configuration, environment);
        var cookieSecurity = configuration.GetSection(CookieSecurityOptions.Section).Get<CookieSecurityOptions>() ?? new CookieSecurityOptions();
        var useStubs = configuration.GetValue<bool>(UseStubsKey);

        services.AddOptions<SessionOptions>().Bind(configuration.GetSection(SessionOptions.Section)).ValidateDataAnnotations()
            .Validate(o => o.IdleTimeout <= o.AbsoluteTimeout, "Session:IdleTimeoutMinutes must not exceed Session:AbsoluteTimeoutHours.")
            .ValidateOnStart();
        services.AddOptions<ApiClientOptions>().Bind(configuration.GetSection(ApiClientOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        var sessionOptions = configuration.GetSection(SessionOptions.Section).Get<SessionOptions>() ?? new SessionOptions();

        // Only Development/Testing may talk plain http to the API; everywhere else a bad address stops the app at startup.
        var apiBase = ApiClientOptions.ResolveBaseAddress(
            configuration[$"{ApiClientOptions.Section}:BaseUrl"] ?? (useStubs ? "https://localhost:7101/api/v1" : null),
            PortalStartupGuards.AllowsInsecureApi(environment));
        if (!apiBase.AbsolutePath.TrimEnd('/').EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            apiBase = new Uri(apiBase, "api/v1/");
        }

        // Session payloads, the cookie ticket and antiforgery tokens are encrypted with this key ring. Behind several nodes (or across
        // restarts) it must be shared and persistent: set DataProtection:KeyPath to a mounted, access-restricted directory
        // (required outside Development/Testing). Protect the directory with OS permissions or a mounted secret store.
        var dataProtection = services.AddDataProtection().SetApplicationName("NexaVerify.Portal");
        if (configuration["DataProtection:KeyPath"] is { Length: > 0 } keyPath)
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        }

        services.AddMemoryCache();
        services.AddPortalCache(configuration);
        services.AddSingleton<ISessionStore, DistributedSessionStore>();
        services.AddSingleton<IMfaPendingStore, DistributedMfaPendingStore>();
        services.AddSingleton<IRefreshTokenExchange, HttpRefreshTokenExchange>();
        services.AddSingleton<TokenRefreshCoordinator>();
        services.AddTransient<SessionBearerHandler>(); // stateless: every dependency is a singleton, so handler lifetime does not matter

        // No HttpClient timeout (each call gets its own in ApiGateway) and no automatic redirects: a 307/308 would replay a password
        // or bearer token to wherever it points, so a redirect is treated as a broken API address instead.
        services.AddHttpClient(ApiClientNames.Anonymous, client => Configure(client, apiBase)).ConfigurePrimaryHttpMessageHandler(NoRedirectHandler);
        services.AddHttpClient(ApiClientNames.Authenticated, client => Configure(client, apiBase))
            .ConfigurePrimaryHttpMessageHandler(NoRedirectHandler)
            .AddHttpMessageHandler<SessionBearerHandler>();

        services.AddScoped<ClientAddress>();
        services.AddScoped<ICurrentSession, AuthenticationStateCurrentSession>();
        services.AddScoped<IApiGateway, ApiGateway>();
        services.AddScoped<IPortalAuth, PortalAuth>();

        if (useStubs)
        {
            services.AddScoped<IAuthApiClient, StubAuthApiClient>();
            services.AddScoped<IPublicApiClient, StubPublicApiClient>();
            services.AddScoped<IDashboardApiClient, StubDashboardApiClient>();
        }
        else
        {
            services.AddScoped<IAuthApiClient, AuthApiClient>();
            services.AddScoped<PublicApiClient>();
            services.AddScoped<IPublicApiClient>(sp => new CachingPublicApiClient(
                sp.GetRequiredService<PublicApiClient>(), sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));
            services.AddScoped<IDashboardApiClient, DashboardApiClient>();
        }

        services.AddScoped<IClientsApiClient, ClientsApiClient>();
        services.AddScoped<ILicensingApiClient, LicensingApiClient>();
        services.AddScoped<IAccessApiClient, AccessApiClient>();
        services.AddScoped<IClientLicenseApiClient, ClientLicenseApiClient>();
        services.AddScoped<IFacesApiClient, FacesApiClient>();
        services.AddScoped<IApiKeysApiClient, ApiKeysApiClient>();
        services.AddScoped<IWebhooksApiClient, WebhooksApiClient>();
        services.AddScoped<IClientAccountApiClient, ClientAccountApiClient>();
        services.AddScoped<INotificationsApiClient, NotificationsApiClient>();

        var cookieName = sessionOptions.EffectiveCookieName(cookieSecurity.RequireSecure);
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = cookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.IsEssential = true;
                options.Cookie.Path = "/";
                options.Cookie.SameSite = cookieSecurity.SameSiteMode;
                options.Cookie.SecurePolicy = cookieSecurity.RequireSecure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                options.LoginPath = "/login";
                options.AccessDeniedPath = "/forbidden";
                options.ReturnUrlParameter = "returnUrl";
                options.ExpireTimeSpan = sessionOptions.AbsoluteTimeout;
                options.SlidingExpiration = false; // the server-side session owns idle/absolute expiry
                options.Events.OnValidatePrincipal = SessionCookieEvents.ValidateAsync;
            });

        var cacheOptions = configuration.GetSection(PortalCacheOptions.Section).Get<PortalCacheOptions>() ?? new PortalCacheOptions();
        services.AddSingleton(sp => new DownloadThrottle(
            sp.GetRequiredService<TimeProvider>(),
            cacheOptions.IsShared ? sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>() : null));
        services.AddPortalAuthorization();
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, SessionAuthenticationStateProvider>();
        return services;
    }

    /// <summary>Portal separation and permission policies (also used by component tests to exercise real authorization).</summary>
    public static IServiceCollection AddPortalAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, PortalRequirementHandler>();
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.PlatformPortal, p => p.RequireAuthenticatedUser().AddRequirements(new PortalRequirement(PortalKinds.Admin, null, false)))
            .AddPolicy(Policies.ClientPortal, p => p.RequireAuthenticatedUser().AddRequirements(new PortalRequirement(PortalKinds.Client, null, false)))
            .AddPolicy(Policies.SignedIn, p => p.RequireAuthenticatedUser().AddRequirements(new PortalRequirement(null, null, true)));
        return services;
    }

    private static HttpMessageHandler NoRedirectHandler() =>
        new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    private static void Configure(HttpClient client, Uri baseAddress)
    {
        client.BaseAddress = baseAddress;
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NexaVerify-Portal/1.0");
    }
}
