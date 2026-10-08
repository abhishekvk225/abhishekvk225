using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NexaVerify.Web;

/// <summary>
/// OpenTelemetry for the portal (same switches as the API: <c>Telemetry:Enabled</c> and <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>). Off by default.
/// Query strings are stripped from spans; health probes are skipped.
/// </summary>
public static class PortalTelemetrySetup
{
    public static IServiceCollection AddPortalTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Telemetry:Enabled"))
        {
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("nexaverify-portal"))
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
