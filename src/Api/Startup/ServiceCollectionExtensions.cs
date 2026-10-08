using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using NexaVerify.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Http.Timeouts;
using NexaVerify.Api.Configuration;
using NexaVerify.Api.Filters;
using NexaVerify.Api.Http;
using NexaVerify.Api.Middleware;
using NexaVerify.Contracts.Common;

namespace NexaVerify.Api.Startup;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApiOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SecurityHeadersOptions>().Bind(configuration.GetSection(SecurityHeadersOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<RequestLimitsOptions>().Bind(configuration.GetSection(RequestLimitsOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<RateLimitSettings>().Bind(configuration.GetSection(RateLimitSettings.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<ForwardedHeadersSettings>().Bind(configuration.GetSection(ForwardedHeadersSettings.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<HostingOptions>().Bind(configuration.GetSection(HostingOptions.SectionName)).ValidateOnStart();
        services.AddOptions<CorsAllowListOptions>().Bind(configuration.GetSection(CorsAllowListOptions.SectionName)).ValidateOnStart();
        return services;
    }

    public static IServiceCollection AddApiControllers(this IServiceCollection services, IConfiguration configuration)
    {
        var limits = configuration.GetSection(RequestLimitsOptions.SectionName).Get<RequestLimitsOptions>() ?? new RequestLimitsOptions();

        services.AddSingleton<ProblemDetailsFactory, NexaProblemDetailsFactory>();
        services
            .AddControllers(options => options.Filters.Add<ValidationFilter>())
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                options.JsonSerializerOptions.MaxDepth = limits.JsonMaxDepth;
            });

        services.AddProblemDetails();
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddRequestTimeouts(options =>
            options.DefaultPolicy = new RequestTimeoutPolicy { Timeout = TimeSpan.FromSeconds(limits.RequestTimeoutSeconds) });
        return services;
    }

    public static IServiceCollection AddApiAuthorization(this IServiceCollection services)
    {
        // Integrators send X-Api-Key; portal users send a bearer token. The policy scheme picks one per request (API key wins if both are present).
        services.AddAuthentication(ApiKeyAuthenticationHandler.PolicyScheme)
            .AddPolicyScheme(ApiKeyAuthenticationHandler.PolicyScheme, "API key or bearer token", options =>
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(HttpHeaderNames.ApiKey) ? ApiKeyAuthenticationHandler.SchemeName : JwtBearerDefaults.AuthenticationScheme)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { })
            .AddJwtBearer();
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, JwtBearerSetup>();

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddAuthorization(options =>
        {
            // Deny by default: every endpoint requires authentication unless it opts out with [AllowAnonymous].
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });
        return services;
    }

    public static IServiceCollection AddApiCors(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var allowed = configuration.GetSection(CorsAllowListOptions.SectionName).Get<CorsAllowListOptions>()?.AllowedOrigins ?? [];
        foreach (var origin in allowed)
        {
            ValidateOrigin(origin, environment);
        }

        services.AddCors(options => options.AddPolicy(CorsAllowListOptions.PolicyName, policy =>
        {
            if (allowed.Length > 0)
            {
                policy.WithOrigins(allowed).AllowAnyHeader().AllowAnyMethod();
            }
        }));
        return services;
    }

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(RateLimitSettings.SectionName).Get<RateLimitSettings>() ?? new RateLimitSettings();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, token) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                var problem = ApiProblem.Create(context.HttpContext, StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited,
                    "Too many requests. Slow down and retry later.");
                context.HttpContext.Response.ContentType = "application/problem+json";
                await context.HttpContext.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", token);
            };

            // Backstop for every request, partitioned by (trusted) client IP.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                RateLimitPartition.GetFixedWindowLimiter(ClientKey(http), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.PerIpPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));

            // Credential endpoints are far stricter (brute force / enumeration).
            options.AddPolicy(RateLimitSettings.AuthPolicy, http =>
                RateLimitPartition.GetFixedWindowLimiter("auth:" + http.Request.Path.Value + ":" + ClientKey(http), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.AuthPerIpPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });
        return services;
    }

    public static IServiceCollection AddApiForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(ForwardedHeadersSettings.SectionName).Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
        if (!settings.Enabled)
        {
            return services;
        }

        if (settings.KnownProxies.Length == 0 && settings.KnownNetworks.Length == 0)
        {
            throw new InvalidOperationException(
                "ForwardedHeaders:Enabled requires ForwardedHeaders:KnownProxies and/or KnownNetworks. Trusting every sender would allow IP and scheme spoofing.");
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = settings.ForwardLimit;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in settings.KnownProxies)
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            foreach (var network in settings.KnownNetworks)
            {
                var parsed = System.Net.IPNetwork.Parse(network);
                if (parsed.PrefixLength == 0)
                {
                    throw new InvalidOperationException("ForwardedHeaders:KnownNetworks must not trust the whole internet (/0).");
                }

                options.KnownIPNetworks.Add(parsed);
            }
        });
        return services;
    }

    /// <summary>The partition key for per-client throttling: the (forwarded-header-corrected) remote IP.</summary>
    public static string ClientKey(HttpContext http)
    {
        var address = http.Connection.RemoteIpAddress;
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        // IPv6: one client can rotate through a whole /64, so partition on the /64 network.
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }

    private static void ValidateOrigin(string origin, IHostEnvironment environment)
    {
        if (origin.Trim() == "*" || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || uri.AbsolutePath != "/" || origin.EndsWith('/') || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException($"Cors:AllowedOrigins must list explicit origins (scheme://host[:port], no path); '{origin}' is not valid and '*' is not permitted.");
        }

        var localDevelopment = environment.IsDevelopment() && uri.IsLoopback;
        if (uri.Scheme != Uri.UriSchemeHttps && !localDevelopment)
        {
            throw new InvalidOperationException($"Cors:AllowedOrigins must use https (http is only allowed for localhost in Development): '{origin}'.");
        }
    }
}
