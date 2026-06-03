using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using RequestProcessor.Diagnostics;
using RequestProcessor.HealthChecks;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Shared Aspire service defaults — applied to every service in the solution.
/// Wires up OpenTelemetry (including the request pool's own ActivitySource and Meter),
/// health checks, and service discovery.
/// </summary>
public static class ServiceDefaultsExtensions
{
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddRuntimeInstrumentation()
                .AddMeter(RequestPoolDiagnostics.MeterName))
            .WithTracing(tracing => tracing
                .AddSource(RequestPoolDiagnostics.ActivitySourceName)
                .AddSource("OrleansSample.Batch"));

        // Export to the Aspire dashboard (or any OTLP-compatible backend) when
        // OTEL_EXPORTER_OTLP_ENDPOINT is set by the AppHost at launch.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry()
                .WithMetrics(m => m.AddOtlpExporter())
                .WithTracing(t => t.AddOtlpExporter());
        }

        return builder;
    }

    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

        return builder;
    }

    /// <summary>
    /// Registers <see cref="RequestPoolHealthCheck"/> with the health check pipeline.
    /// Call this on services that host a request pool to expose queue-depth health.
    /// </summary>
    public static IHostApplicationBuilder AddRequestPoolHealthCheck(
        this IHostApplicationBuilder builder,
        string name = "request_pool",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        Action<RequestPoolHealthCheckOptions>? configure = null)
    {
        builder.Services.AddHealthChecks()
            .AddRequestPool(name, failureStatus, tags, configure);

        return builder;
    }

    /// <summary>
    /// Maps <c>/health</c> (all checks) and <c>/alive</c> (checks tagged "live").
    /// Call this on every ASP.NET Core web application so Aspire can observe readiness.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });
        return app;
    }
}
