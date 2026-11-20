using System.Diagnostics;
using System.Diagnostics.Metrics;
using Ignixa.Federation.Core.Diagnostics;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Diagnostics;

public sealed class FederationTelemetryTests
{
    [Fact]
    public void Name_IsTheDocumentedSourceName()
    {
        FederationTelemetry.Name.ShouldBe("Ignixa.Federation");
        FederationTelemetry.ActivitySource.Name.ShouldBe("Ignixa.Federation");
        FederationTelemetry.Meter.Name.ShouldBe("Ignixa.Federation");
    }

    [Fact]
    public void ActivitySource_DeliversActivitiesToListenersOfTheSourceName()
    {
        var started = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Ignixa.Federation",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = started.Add,
        };
        ActivitySource.AddActivityListener(listener);

        using Activity? activity = FederationTelemetry.ActivitySource.StartActivity("probe");

        activity.ShouldNotBeNull();
        started.ShouldContain(activity);
    }

    [Fact]
    public void Meter_PublishesInstrumentsToListeners()
    {
        var instrumentNames = new List<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == "Ignixa.Federation")
                {
                    instrumentNames.Add(instrument.Name);
                }
            },
        };
        listener.Start();

        Counter<long> counter = FederationTelemetry.Meter.CreateCounter<long>("federation.test.probe");

        instrumentNames.ShouldContain(counter.Name);
    }

    [Fact]
    public void CoreAssembly_HasNoHttpAspNetOrHostingReferences()
    {
        string[] forbidden = typeof(FederationTelemetry).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n == "System.Net.Http"
                || n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                || n.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal))
            .ToArray();

        forbidden.ShouldBeEmpty("Core is the pure domain layer and must not depend on HTTP, ASP.NET Core or hosting.");
    }
}
