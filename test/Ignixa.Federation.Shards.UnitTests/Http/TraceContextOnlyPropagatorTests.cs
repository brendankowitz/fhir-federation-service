using System.Diagnostics;
using Ignixa.Federation.Shards.Http;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Http;

public sealed class TraceContextOnlyPropagatorTests
{
    [Fact]
    public void AnAmbientPropagatorCannotSupplyTheReservedCallerHeader()
    {
        var propagator = new TraceContextOnlyPropagator(new CustomPropagator());
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        propagator.Inject(activity: null, headers, (_, name, value) => headers.Add(name, value));

        headers.Keys.ShouldBe(["traceparent"]);
        propagator.Fields.ShouldBe(["traceparent"]);
    }

    private sealed class CustomPropagator : DistributedContextPropagator
    {
        public override IReadOnlyCollection<string> Fields { get; } = ["traceparent", "x-federation-caller"];

        public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter)
        {
            setter?.Invoke(carrier, "traceparent", "synthetic-trace");
            setter?.Invoke(carrier, "x-federation-caller", "synthetic-ambient-caller");
        }

        public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState) =>
            DistributedContextPropagator.CreateDefaultPropagator().ExtractTraceIdAndState(carrier, getter, out traceId, out traceState);

        public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter) =>
            DistributedContextPropagator.CreateDefaultPropagator().ExtractBaggage(carrier, getter);
    }
}
