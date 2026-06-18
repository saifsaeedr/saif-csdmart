using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Dmart.Config;

// Minimal, opt-in observability. Wires the runtime's BUILT-IN meters and
// activity sources (ASP.NET Core, Kestrel, Npgsql) to an OTLP collector — no
// custom instrumentation, so nothing to maintain and no AOT reflection.
//
// Fully gated on DmartSettings.OtlpEndpoint: empty (the default) means this is
// a no-op and zero OpenTelemetry machinery is registered, so existing
// deployments and tests pay nothing.
public static class Telemetry
{
    public static void AddDmartTelemetry(this WebApplicationBuilder builder, DmartSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.OtlpEndpoint)) return;
        if (!Uri.TryCreate(settings.OtlpEndpoint, UriKind.Absolute, out var endpoint)) return;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("dmart"))
            .WithMetrics(m => m
                .AddMeter(
                    "Microsoft.AspNetCore.Hosting",
                    "Microsoft.AspNetCore.Server.Kestrel",
                    "System.Net.Http",
                    "Npgsql")
                .AddOtlpExporter(o => o.Endpoint = endpoint))
            .WithTracing(t => t
                // ASP.NET Core (.NET 8+) and Npgsql emit activities on these
                // sources without any instrumentation package.
                .AddSource("Microsoft.AspNetCore", "Npgsql")
                .AddOtlpExporter(o => o.Endpoint = endpoint));
    }
}
