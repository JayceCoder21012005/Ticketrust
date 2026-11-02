using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Cross-cutting defaults shared by every .NET service: OpenTelemetry, health checks,
/// service discovery and standard HTTP resilience.
/// </summary>
public static class Extensions
{
    public const string LivenessPath = "/health";
    public const string ReadinessPath = "/alive";

    /// <summary>Tag for checks that only prove the process is alive (no dependencies).</summary>
    public const string LiveTag = "live";

    /// <summary>Custom activity sources and meters (e.g. the outbox) register under this prefix.</summary>
    public const string TelemetryPrefix = "TicketRush";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
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

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter($"{TelemetryPrefix}.*"))
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddSource($"{TelemetryPrefix}.*")
                .AddAspNetCoreInstrumentation(options =>
                    // Probes would drown real traffic in the trace view.
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments(LivenessPath, StringComparison.OrdinalIgnoreCase)
                        && !context.Request.Path.StartsWithSegments(ReadinessPath, StringComparison.OrdinalIgnoreCase))
                .AddHttpClientInstrumentation());

        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag]);

        return builder;
    }

    /// <summary>
    /// <c>/health</c> = liveness (process only), <c>/alive</c> = readiness (all checks, incl. postgres + redis).
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(LivenessPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(LiveTag),
        });

        app.MapHealthChecks(ReadinessPath, new HealthCheckOptions
        {
            Predicate = _ => true,
        });

        return app;
    }
}
