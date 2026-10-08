using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NexaVerify.Api.Startup;

/// <summary>
/// OpenTelemetry traces and metrics, exported over OTLP to a collector (see deploy/observability). Off by default; switch on with
/// <c>Telemetry:Enabled=true</c> and point the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> variable at the collector.
/// Nothing sensitive is recorded: query strings are stripped from spans, exceptions are not attached, health probes are skipped.
/// </summary>
public static class TelemetrySetup
{
    public static IServiceCollection AddApiTelemetry(this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        if (!configuration.GetValue<bool>("Telemetry:Enabled"))
        {
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter())
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.RecordException = false;
                    options.Filter = http => !http.Request.Path.StartsWithSegments("/health");
                    options.EnrichWithHttpResponse = (activity, _) => activity.SetTag("url.query", null);
                })
                .AddHttpClientInstrumentation()
                .AddOtlpExporter());
        return services;
    }
}
