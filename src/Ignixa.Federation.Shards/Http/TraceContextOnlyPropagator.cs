using System.Diagnostics;

namespace Ignixa.Federation.Shards.Http;

/// <summary>
/// Keeps trace correlation but withholds baggage (<c>baggage</c> and <c>Correlation-Context</c>) from the shard.
/// No propagator may supply the caller header; that value must come from an explicit request binding.
/// </summary>
internal sealed class TraceContextOnlyPropagator : DistributedContextPropagator
{
    private readonly DistributedContextPropagator _inner;

    public TraceContextOnlyPropagator(DistributedContextPropagator inner)
    {
        _inner = inner;
        Fields = [.. inner.Fields.Where(field => !IsExcluded(field))];
    }

    public override IReadOnlyCollection<string> Fields { get; }

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter) =>
        _inner.Inject(
            activity,
            carrier,
            setter is null ? null : (target, name, value) =>
            {
                if (!IsExcluded(name))
                {
                    setter(target, name, value);
                }
            });

    public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState) =>
        _inner.ExtractTraceIdAndState(carrier, getter, out traceId, out traceState);

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter) =>
        _inner.ExtractBaggage(carrier, getter);

    private static bool IsExcluded(string name) =>
        name.Equals("baggage", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Correlation-Context", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(ShardRequestContext.CallerHeader, StringComparison.OrdinalIgnoreCase);
}
