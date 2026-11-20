using System.Diagnostics;

namespace Ignixa.Federation.Shards.Http;

/// <summary>
/// Injects what another propagator injects except baggage, the W3C <c>baggage</c> or legacy <c>Correlation-Context</c>
/// field: baggage is arbitrary application data, and a shard is a separate trust domain. Trace correlation is unchanged.
/// </summary>
internal sealed class TraceContextOnlyPropagator : DistributedContextPropagator
{
    private readonly DistributedContextPropagator _inner;

    public TraceContextOnlyPropagator(DistributedContextPropagator inner)
    {
        _inner = inner;
        Fields = [.. inner.Fields.Where(field => !IsBaggage(field))];
    }

    public override IReadOnlyCollection<string> Fields { get; }

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter) =>
        _inner.Inject(
            activity,
            carrier,
            setter is null ? null : (target, name, value) =>
            {
                if (!IsBaggage(name))
                {
                    setter(target, name, value);
                }
            });

    public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState) =>
        _inner.ExtractTraceIdAndState(carrier, getter, out traceId, out traceState);

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter) =>
        _inner.ExtractBaggage(carrier, getter);

    private static bool IsBaggage(string name) =>
        name.Equals("baggage", StringComparison.OrdinalIgnoreCase) || name.Equals("Correlation-Context", StringComparison.OrdinalIgnoreCase);
}
