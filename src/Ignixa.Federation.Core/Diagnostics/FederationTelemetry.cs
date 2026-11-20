using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Ignixa.Federation.Core.Diagnostics;

/// <summary>
/// Central OpenTelemetry-compatible instrumentation sources for Ignixa Federation (design spec §14).
/// Every span and metric emitted by the federation pipeline is created from these two instances so a single
/// <c>AddSource</c>/<c>AddMeter</c> registration of <see cref="Name"/> captures all of it.
/// </summary>
public static class FederationTelemetry
{
    /// <summary>
    /// The name shared by <see cref="ActivitySource"/> and <see cref="Meter"/> (design spec §14).
    /// Hosts register this value with their OpenTelemetry tracer and meter providers.
    /// </summary>
    public const string Name = "Ignixa.Federation";

    private static readonly string? InformationalVersion = typeof(FederationTelemetry).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>
    /// Gets the <see cref="System.Diagnostics.ActivitySource"/> for federation traces (design spec §14).
    /// Its version is the assembly informational version, when one is available.
    /// </summary>
    public static ActivitySource ActivitySource { get; } = new(Name, InformationalVersion);

    /// <summary>
    /// Gets the <see cref="System.Diagnostics.Metrics.Meter"/> for federation metrics (design spec §14).
    /// Its version is the assembly informational version, when one is available.
    /// </summary>
    public static Meter Meter { get; } = new(Name, InformationalVersion);
}
