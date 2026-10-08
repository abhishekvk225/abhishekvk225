using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using NexaVerify.Web.Services;

namespace NexaVerify.Web.Security;

public static class PortalServiceExtensions
{
    /// <summary>Config key that turns on the design-review stub clients (Development/UiDemo only).</summary>
    public const string UseStubsKey = "Ui:UseStubClients";

    /// <summary>
    /// Registers the BFF: server-side sessions, the opaque HttpOnly cookie, policies, HTTP clients and the typed API clients.
    /// </summary>
    public static IServiceCollection AddPortalBff(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var cookieSecurity = configuration.GetSection(CookieSecurityOptions.Section).Get<CookieSecurityOptions>() ?? new CookieSecurityOptions();

        var useStubs = configuration.GetValue<bool>(UseStubsKey);
        if (useStubs && !(environment.IsDevelopment() || environment.IsEnvironment("UiDemo")))
        {
            throw new InvalidOperationException($"{UseStubsKey} accepts any password and is only allowed in Development or UiDemo.");
        }

        services.AddOptions<SessionOptions>().Bind(configuration.GetSection(SessionOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<ApiClientOptions>().Bind(configuration.GetSection(ApiClientOptions.Section)).ValidateDataAnnotations();
        var sessionOptions = configuration.GetSection(SessionOptions.Section).Get<SessionOptions>() ?? new SessionOptions();

        // Only Development/Testing may talk plain http to the API; everywhere else a bad address stops the app at startup.
        var allowInsecure = environment.IsDevelopment() || environment.IsEnvironment("Testing") || environment.IsEnvironment("UiDemo");
        var apiBase = ApiClientOptions.ResolveBaseAddress(configuration[$"{ApiClientOptions.Section}:BaseUrl"] ?? (useStubs ? "https://localhost:7101/api/v1" : null), allowInsecure);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue<int?>($"{ApiClientOptions.Section}:TimeoutSeconds") ?? 30, 1, 300));
        if (!apiBase.AbsolutePath.TrimEnd('/').EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            apiBase = new Uri(apiBase, "api/v1/");
        }

        // Session payloads, the cookie ticket and antiforgery tokens are encrypted with this key ring. Behind several nodes (or across
        // restarts) it must be shared and persistent: set DataProtection:KeyPath to a mounted, access-restricted directory.
        var dataProtection = services.AddDataProtection().SetApplicationName("NexaVerify.Portal");
        if (configuration["DataProtection:KeyPath"] is { Length: > 0 } keyPath)
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        }

        services.AddDistributedMemoryCache();
        services.AddSingleton<ISessionStore, DistributedSessionStore>();
        services.AddSingleton<IRefreshTokenExchange, HttpRefreshTokenExchange>();
        services.AddSingleton<TokenRefreshCoordinator>();
        services.AddTransient<SessionBearerHandler>();

        services.AddHttpClient(ApiClientNames.Anonymous, client => Configure(client, apiBase, timeout));
        services.AddHttpClient(ApiClientNames.Authenticated, client => Configure(client, apiBase, timeout))
            .AddHttpMessageHandler<SessionBearerHandler>();

        services.AddScoped<ICurrentSession, AuthenticationStateCurrentSession>();
        services.AddScoped<IApiGateway, ApiGateway>();
        services.AddScoped<IPortalAuth, PortalAuth>();

        if (useStubs)
        {
            services.AddScoped<IAuthApiClient, StubAuthApiClient>();
            services.AddScoped<IDashboardApiClient, StubDashboardApiClient>();
        }
        else
        {
            services.AddScoped<IAuthApiClient, AuthApiClient>();
            services.AddScoped<IDashboardApiClient, DashboardApiClient>();
        }

        services.AddScoped<IClientsApiClient, ClientsApiClient>();
        services.AddScoped<ILicensingApiClient, LicensingApiClient>();
        services.AddScoped<IAccessApiClient, AccessApiClient>();

        var cookieName = sessionOptions.CookieName;
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

    private static void Configure(HttpClient client, Uri baseAddress, TimeSpan timeout)
    {
        client.BaseAddress = baseAddress;
        client.Timeout = timeout;
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NexaVerify-Portal/1.0");
    }
}
