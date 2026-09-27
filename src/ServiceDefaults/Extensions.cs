using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Shared setup for every Athlos service: OpenTelemetry, health checks, service discovery,
// resilience, ProblemDetails, and the /health and /version endpoints (design §6, §7.1).
public static class Extensions
{
    private const string HealthPath = "/health";
    private const string LivePath = "/health/live";
    private const string ReadyPath = "/health/ready";
    private const string VersionPath = "/version";

    // Checks tagged "live" run on /health/live; every check runs on /health/ready.
    public const string LiveTag = "live";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddProblemDetails();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    // The release tag (for example v0.3.0 or sha-3f2a1c9), set by the deploy as APP_VERSION (design §12.2).
    public static string GetAppVersion(this IConfiguration configuration) =>
        configuration["APP_VERSION"] is { Length: > 0 } version ? version : "local";

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var version = builder.Configuration.GetAppVersion();

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddAttributes([new("service.version", version)]))
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("Athlos.*");
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddSource("Athlos.*")
                    .AddAspNetCoreInstrumentation(options =>
                        options.Filter = context => !context.Request.Path.StartsWithSegments(HealthPath))
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        // Local: the Aspire dashboard sets OTEL_EXPORTER_OTLP_ENDPOINT.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Azure: Bicep sets the Application Insights connection string on each container app.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            builder.Services.AddOpenTelemetry().UseAzureMonitor();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag]);

        return builder;
    }

    // Health endpoints are mapped in every environment because Container Apps probes use them.
    // They return only the status, never check details.
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(LivePath, new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(LiveTag),
        });

        app.MapHealthChecks(ReadyPath);

        var version = app.Configuration.GetAppVersion();
        app.MapGet(VersionPath, () => TypedResults.Ok(new { version }))
            .ExcludeFromDescription();

        return app;
    }
}
