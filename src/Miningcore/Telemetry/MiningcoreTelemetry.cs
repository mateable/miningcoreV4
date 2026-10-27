using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Miningcore.Telemetry;

public static class MiningcoreTelemetry
{
    public static readonly ActivitySource ActivitySource = new(
        "Miningcore",
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0");

    public static readonly string ServiceName = "miningcore";
    public static readonly string ServiceVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0";

    /// <summary>
    /// Adds OpenTelemetry tracing to the service collection.
    /// </summary>
    /// <param name="otlpEndpoint">
    /// OTLP collector endpoint (e.g. "http://localhost:4317").
    /// When null or empty, tracing is disabled with zero overhead (AlwaysOffSampler).
    /// Falls back to OTEL_EXPORTER_OTLP_ENDPOINT env var if set in config but not provided.
    /// </param>
    public static IServiceCollection AddMiningcoreTelemetry(this IServiceCollection services, string otlpEndpoint = null)
    {
        if(string.IsNullOrEmpty(otlpEndpoint))
            otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

        if(string.IsNullOrEmpty(otlpEndpoint))
        {
            // No endpoint — zero overhead AlwaysOffSampler
            services.AddOpenTelemetry()
                .WithTracing(builder => builder
                    .SetResourceBuilder(ResourceBuilder.CreateDefault()
                        .AddService(ServiceName, serviceVersion: ServiceVersion))
                    .AddSource("Miningcore")
                    .SetSampler(new AlwaysOffSampler()));

            return services;
        }

        services.AddOpenTelemetry()
            .WithTracing(builder =>
            {
                builder
                    .SetResourceBuilder(ResourceBuilder.CreateDefault()
                        .AddService(ServiceName, serviceVersion: ServiceVersion))
                    .AddSource("Miningcore")
                    .AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(otlpEndpoint);
                    });
            });

        return services;
    }
}
