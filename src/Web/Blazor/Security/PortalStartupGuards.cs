using System.Net;

namespace NexaVerify.Web.Security;

/// <summary>
/// Refuses to start in unsafe configurations. Only Development is relaxed; Testing may use plain http to a local API and skip the
/// deployment-only requirements; every other environment name (Staging, Production, UiDemo, anything) gets the full rules, so an
/// environment name can never be used to switch protections off.
/// </summary>
public static class PortalStartupGuards
{
    public const string DemoEnvironment = "UiDemo";
    public const string TestingEnvironment = "Testing";

    public static bool AllowsInsecureApi(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment(TestingEnvironment);

    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        var development = environment.IsDevelopment();

        // Stub clients accept any password.
        if (configuration.GetValue<bool>(PortalServiceExtensions.UseStubsKey))
        {
            var demoAllowed = environment.IsEnvironment(DemoEnvironment) && configuration.GetValue<bool>(PortalServiceExtensions.AllowDemoStubsKey);
            if (!development && !demoAllowed)
            {
                throw new InvalidOperationException(
                    $"{PortalServiceExtensions.UseStubsKey} accepts any password: allowed in Development, or in a {DemoEnvironment} design-review host that also sets {PortalServiceExtensions.AllowDemoStubsKey}=true.");
            }
        }

        var cookies = configuration.GetSection(CookieSecurityOptions.Section).Get<CookieSecurityOptions>() ?? new CookieSecurityOptions();
        var session = configuration.GetSection(SessionOptions.Section).Get<SessionOptions>() ?? new SessionOptions();
        if (session.EffectiveCookieName(cookies.RequireSecure).StartsWith("__Host-", StringComparison.Ordinal) && !cookies.RequireSecure)
        {
            throw new InvalidOperationException("A __Host- cookie name needs Security:Cookies:RequireSecure=true (browsers drop it otherwise).");
        }

        if (development)
        {
            return;
        }

        if (!cookies.RequireSecure)
        {
            throw new InvalidOperationException("Security:Cookies:RequireSecure must be true outside Development.");
        }

        if (!string.Equals(configuration[$"{CookieSecurityOptions.Section}:SameSite"] ?? "Strict", "Strict", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Security:Cookies:SameSite must be Strict outside Development.");
        }

        var headers = configuration.GetSection(SecurityHeadersOptions.Section).Get<SecurityHeadersOptions>() ?? new SecurityHeadersOptions();
        if (!headers.Enabled || !headers.ContentSecurityPolicyEnabled || headers.AllowInsecureWebSockets)
        {
            throw new InvalidOperationException("Security:Headers must stay enabled with the Content-Security-Policy on and plain ws: refused outside Development.");
        }

        if (configuration["AllowedHosts"] is null or "" or "*")
        {
            throw new InvalidOperationException("AllowedHosts must list the real host names outside Development ('*' disables host-header validation).");
        }

        if (environment.IsEnvironment(TestingEnvironment))
        {
            return; // test hosts: ephemeral keys and the in-memory store are fine
        }

        if (string.IsNullOrWhiteSpace(configuration["DataProtection:KeyPath"]))
        {
            throw new InvalidOperationException("DataProtection:KeyPath must point at a persistent, shared, access-restricted directory outside Development (otherwise every restart or second node invalidates all sessions).");
        }

        if (!session.AllowInMemoryStore)
        {
            throw new InvalidOperationException("Sessions are kept in this process's memory (lost on restart, not shared between nodes). Set Session:AllowInMemoryStore=true to accept that explicitly, for a single node or sticky sessions.");
        }
    }
}

/// <summary>Trusted-proxy settings (<c>ForwardedHeaders:*</c>) with the same rules as the API's.</summary>
public static class ForwardedHeadersSetup
{
    public static bool Configure(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("ForwardedHeaders");
        if (!section.GetValue<bool>("Enabled"))
        {
            return false;
        }

        var proxies = section.GetSection("KnownProxies").Get<string[]>() ?? [];
        var networks = section.GetSection("KnownNetworks").Get<string[]>() ?? [];
        if (proxies.Length == 0 && networks.Length == 0)
        {
            throw new InvalidOperationException("ForwardedHeaders:Enabled requires ForwardedHeaders:KnownProxies and/or KnownNetworks. Trusting every sender would allow IP and scheme spoofing.");
        }

        var parsedProxies = proxies.Select(p => IPAddress.TryParse(p, out var ip)
            ? ip
            : throw new InvalidOperationException($"ForwardedHeaders:KnownProxies contains '{p}', which is not an IP address.")).ToList();
        var parsedNetworks = networks.Select(n => IPNetwork.TryParse(n, out var network)
            ? network
            : throw new InvalidOperationException($"ForwardedHeaders:KnownNetworks contains '{n}', which is not a CIDR range such as 10.0.0.0/8.")).ToList();
        if (parsedNetworks.Any(n => n.PrefixLength == 0))
        {
            throw new InvalidOperationException("ForwardedHeaders:KnownNetworks must not trust the whole internet (/0).");
        }

        var limit = section.GetValue<int?>("ForwardLimit") ?? 1;
        if (limit is < 1 or > 10)
        {
            throw new InvalidOperationException("ForwardedHeaders:ForwardLimit must be between 1 and 10.");
        }

        services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = limit;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in parsedProxies)
            {
                options.KnownProxies.Add(proxy);
            }

            foreach (var network in parsedNetworks)
            {
                options.KnownIPNetworks.Add(network);
            }
        });
        return true;
    }
}
