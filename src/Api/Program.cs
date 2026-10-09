using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NexaVerify.Api.Configuration;
using NexaVerify.Api.Http;
using NexaVerify.Api.Logging;
using NexaVerify.Api.Middleware;
using NexaVerify.Api.Startup;
using NexaVerify.Application;
using NexaVerify.Application.Abstractions;
using NexaVerify.Infrastructure;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.Infrastructure.Persistence.Guards;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Docker/Kubernetes secrets: every file in the secrets directory is a setting (file name "Jwt__SigningKeyPem" = key Jwt:SigningKeyPem).
// Applied after environment variables, so a mounted secret wins. The directory is optional (absent in IIS / local runs).
builder.Configuration.AddKeyPerFile(builder.Configuration["NEXAVERIFY_SECRETS_DIR"] ?? "/run/secrets", optional: true);

if (builder.Environment.IsProduction() && builder.Configuration["AllowedHosts"] is null or "" or "*")
{
    throw new InvalidOperationException("AllowedHosts must list the real host names in Production ('*' disables host-header validation).");
}

builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "NexaVerify.Api")
    .Destructure.With<SensitiveDataDestructuringPolicy>());

builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    var limits = context.Configuration.GetSection(RequestLimitsOptions.SectionName).Get<RequestLimitsOptions>() ?? new RequestLimitsOptions();
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = limits.MaxRequestBodyBytes;
    kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(limits.RequestHeadersTimeoutSeconds);
    kestrel.Limits.MaxRequestHeadersTotalSize = limits.MaxRequestHeadersTotalSizeBytes;
    kestrel.Limits.MinRequestBodyDataRate = new Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate(limits.MinRequestBodyBytesPerSecond, TimeSpan.FromSeconds(10));
});

builder.Services.AddApiOptions(builder.Configuration);
builder.Services.AddHttpContextAccessor();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
// Must come after AddInfrastructure: replaces its anonymous fallback with the HTTP-backed principal.
builder.Services.Replace(ServiceDescriptor.Scoped<ICurrentUser, HttpCurrentUser>());
builder.Services.Replace(ServiceDescriptor.Scoped<IRequestInfo, HttpRequestInfo>());

builder.Services.AddApiControllers(builder.Configuration, builder.Environment);
builder.Services.AddApiAuthorization();
builder.Services.AddApiCors(builder.Configuration, builder.Environment);
builder.Services.AddApiRateLimiting(builder.Configuration);
builder.Services.AddApiForwardedHeaders(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database", tags: ["ready"])
    .AddCheck<TenantProtectionHealthCheck>("tenant-protection", tags: ["ready"]);
builder.Services.AddOpenApi();
builder.Services.AddApiTelemetry(builder.Configuration, "nexaverify-api");

var app = builder.Build();

// Fail fast: key material and production-unsafe switches are validated at startup, not on the first request.
_ = app.Services.GetRequiredService<NexaVerify.Infrastructure.Identity.JwtKeyProvider>();
_ = app.Services.GetRequiredService<NexaVerify.Infrastructure.Security.MasterKeyProvider>();
if (app.Environment.IsProduction())
{
    var unsafeSwitches = new[] { "Jwt:AllowEphemeralKey", "Encryption:AllowEphemeralKey", "Email:LogBodies", "Webhooks:AllowUnsafeTargets" }
        .Where(key => app.Configuration.GetValue<bool>(key))
        .ToList();
    if (unsafeSwitches.Count > 0)
    {
        throw new InvalidOperationException("These settings are development-only and must be off in Production: " + string.Join(", ", unsafeSwitches));
    }
}

// The SSRF-guard bypass is for local development and tests only: any other environment (Staging included) refuses it.
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing") && app.Configuration.GetValue<bool>("Webhooks:AllowUnsafeTargets"))
{
    throw new InvalidOperationException("Webhooks:AllowUnsafeTargets is only permitted in the Development and Testing environments.");
}

// The simulated payment provider grants credits on request: it must never run anywhere but a developer machine or the test host.
if (app.Configuration.GetValue<string>("Billing:Provider") is { } billingProvider
    && string.Equals(billingProvider, NexaVerify.Application.Billing.PaymentProviders.Simulated, StringComparison.OrdinalIgnoreCase)
    && !NexaVerify.Infrastructure.Billing.SimulatedProvider.IsAllowedIn(app.Environment))
{
    throw new InvalidOperationException("Billing:Provider=Simulated is only permitted in the Development and Testing environments.");
}

var forwarded = app.Services.GetRequiredService<IOptions<ForwardedHeadersSettings>>().Value;
if (forwarded.Enabled)
{
    app.UseForwardedHeaders();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseExceptionHandler();

if (app.Services.GetRequiredService<IOptions<HostingOptions>>().Value.RedirectToHttps)
{
    app.UseMiddleware<HttpsEnforcementMiddleware>();
}

app.UseSerilogRequestLogging(options =>
{
    // Route template only: raw paths/queries can carry identifiers or PII.
    options.MessageTemplate = "HTTP {RequestMethod} {RouteTemplate} responded {StatusCode} in {Elapsed:0.0000} ms";
    options.EnrichDiagnosticContext = (diagnostic, http) =>
    {
        diagnostic.Set("RouteTemplate", (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched");
        var user = http.RequestServices.GetService<ICurrentUser>();
        if (user is { IsAuthenticated: true })
        {
            diagnostic.Set("ActorType", user.ActorType.ToString());
            diagnostic.Set("ActorId", user.ActorId);
            diagnostic.Set("ClientId", user.ClientId);
        }
    };
});

app.UseStatusCodePages(async context =>
{
    var http = context.HttpContext;
    if (http.Response.HasStarted || http.Response.ContentLength > 0 || !string.IsNullOrEmpty(http.Response.ContentType))
    {
        return;
    }

    var status = http.Response.StatusCode;
    var problem = ApiProblem.Create(http, status, ApiProblem.CodeForStatus(status), "The request could not be completed.");
    http.Response.ContentType = "application/problem+json";
    await http.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
});

app.UseRateLimiter();
app.UseRequestTimeouts();

if (app.Environment.IsDevelopment())
{
    // Interactive docs exist only in Development. Registered before authorization so the static UI assets load.
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "NexaVerify API v1"));
}

app.UseCors(CorsAllowListOptions.PolicyName);
app.UseAuthentication();
app.UseMiddleware<ApiUsageMiddleware>();
app.UseAuthorization();
app.UseMiddleware<RequestContextLoggingMiddleware>();

app.MapControllers();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
    },
}).AllowAnonymous();

app.Run();

/// <summary>Entry point marker so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
