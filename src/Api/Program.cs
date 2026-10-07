using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NexaVerify.Api.Configuration;
using NexaVerify.Api.Filters;
using NexaVerify.Api.Http;
using NexaVerify.Api.Logging;
using NexaVerify.Api.Middleware;
using NexaVerify.Application;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Common;
using NexaVerify.Contracts.Common;
using NexaVerify.Infrastructure;
using NexaVerify.Infrastructure.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "NexaVerify.Api")
    .Destructure.With<SensitiveDataDestructuringPolicy>());

builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    kestrel.AddServerHeader = false;
    var limits = context.Configuration.GetSection(RequestLimitsOptions.SectionName).Get<RequestLimitsOptions>() ?? new RequestLimitsOptions();
    kestrel.Limits.MaxRequestBodySize = limits.MaxRequestBodyBytes;
});

// Typed, validated configuration (fail fast at startup).
builder.Services.AddOptions<SecurityHeadersOptions>().Bind(builder.Configuration.GetSection(SecurityHeadersOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<RequestLimitsOptions>().Bind(builder.Configuration.GetSection(RequestLimitsOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<HostingOptions>().Bind(builder.Configuration.GetSection(HostingOptions.SectionName)).ValidateOnStart();
builder.Services.AddOptions<CorsAllowListOptions>().Bind(builder.Configuration.GetSection(CorsAllowListOptions.SectionName)).ValidateOnStart();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>(); // overrides Infrastructure's anonymous default

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services
    .AddControllers(options => options.Filters.Add<ValidationFilter>())
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
    .ConfigureApiBehaviorOptions(options =>
    {
        // Model-binding failures use the same problem format as everything else.
        options.InvalidModelStateResponseFactory = context =>
        {
            var errors = context.ModelState
                .Where(kv => kv.Value?.Errors.Count > 0)
                .ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value!.Errors.Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? "Invalid value." : e.ErrorMessage).ToArray());
            var problem = ApiProblem.Create(context.HttpContext, Error.Validation("One or more validation errors occurred.", errors));
            return new ObjectResult(problem) { StatusCode = problem.Status, ContentTypes = { "application/problem+json" } };
        };
    });

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddAuthentication(NoAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, NoAuthenticationHandler>(NoAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization(options =>
{
    // Deny by default: every endpoint requires authentication unless it opts out with [AllowAnonymous].
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

var allowedOrigins = builder.Configuration.GetSection(CorsAllowListOptions.SectionName).Get<CorsAllowListOptions>()?.AllowedOrigins ?? [];
if (allowedOrigins.Any(o => o.Trim() == "*"))
{
    throw new InvalidOperationException("Cors:AllowedOrigins must list explicit origins; '*' is not permitted.");
}

builder.Services.AddCors(options => options.AddPolicy(CorsAllowListOptions.PolicyName, policy =>
{
    if (allowedOrigins.Length > 0)
    {
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
    }
}));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

builder.Services.AddOpenApi();

var app = builder.Build();

var hosting = app.Services.GetRequiredService<IOptions<HostingOptions>>().Value;

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

if (hosting.RedirectToHttps)
{
    app.UseHttpsRedirection();
}

app.UseSerilogRequestLogging(options =>
{
    // Route template only: raw paths/queries can carry identifiers or PII.
    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms";
    options.EnrichDiagnosticContext = (diagnostic, http) =>
        diagnostic.Set("RouteTemplate", http.GetEndpoint()?.DisplayName ?? "unmatched");
});

app.UseStatusCodePages(async context =>
{
    var http = context.HttpContext;
    if (http.Response.HasStarted || http.Response.ContentLength > 0 || !string.IsNullOrEmpty(http.Response.ContentType))
    {
        return;
    }

    var code = http.Response.StatusCode switch
    {
        401 => ErrorCodes.Unauthenticated,
        403 => ErrorCodes.Forbidden,
        404 => ErrorCodes.NotFound,
        405 => ErrorCodes.NotFound,
        _ => ErrorCodes.InternalError,
    };
    var problem = ApiProblem.Create(http, http.Response.StatusCode, code, "The request could not be completed.");
    http.Response.ContentType = "application/problem+json";
    await http.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
});

if (!app.Environment.IsProduction())
{
    // Interactive docs are never mapped in production. Registered before authorization so the static UI assets load;
    // the OpenAPI document endpoint itself is mapped below.
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "NexaVerify API v1"));
}

app.UseCors(CorsAllowListOptions.PolicyName);
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<RequestContextLoggingMiddleware>();

app.MapControllers();

if (!app.Environment.IsProduction())
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
