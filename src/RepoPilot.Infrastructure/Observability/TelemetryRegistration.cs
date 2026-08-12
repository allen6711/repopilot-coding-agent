using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace RepoPilot.Infrastructure.Observability;

/// <summary>
/// Wires OpenTelemetry for runs, stages, capability invocations, and provider
/// usage (Principle IV).
/// <para>
/// Deliberately does not add ASP.NET Core instrumentation. Infrastructure has no
/// business knowing a web host exists — the Api project adds that itself, which
/// also keeps the evaluation CLI from dragging in web instrumentation it never
/// uses.
/// </para>
/// </summary>
public static class TelemetryRegistration
{
    /// <summary>
    /// Registers tracing and metrics against the single RepoPilot source and
    /// meter.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="otlpEndpoint">
    /// OTLP endpoint, or null to export to the console. The console exporter is
    /// the development default so a run's trace is readable without standing up
    /// a backend — an unread trace is the same as no trace.
    /// </param>
    public static IServiceCollection AddRepoPilotTelemetry(
        this IServiceCollection services,
        string? otlpEndpoint = null)
    {
        var resource = ResourceBuilder.CreateDefault()
            .AddService(serviceName: "repopilot", serviceVersion: "0.1.0");

        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing.SetResourceBuilder(resource)
                       .AddSource(Telemetry.ActivitySourceName)
                       ;

                if (otlpEndpoint is null)
                {
                    tracing.AddConsoleExporter();
                }
                else
                {
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.SetResourceBuilder(resource)
                       .AddMeter(Telemetry.MeterName)
                       ;

                if (otlpEndpoint is null)
                {
                    metrics.AddConsoleExporter();
                }
                else
                {
                    metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
                }
            });

        return services;
    }
}
