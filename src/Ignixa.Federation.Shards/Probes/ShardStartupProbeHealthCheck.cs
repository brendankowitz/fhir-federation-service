using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ignixa.Federation.Shards.Probes;

/// <summary>
/// Reports whether every required shard method has shown strict handling and a replay, from the worker's published
/// outcomes alone. It calls no backend and touches no request meter.
/// </summary>
/// <remarks>
/// The data holds one <c>"{alias}/{method}"</c> entry per required method, valued <c>strict=&lt;outcome|NotTested&gt;;replay=&lt;outcome|NotTested&gt;</c>.
/// This is behavioral evidence from one sample, not software identity, certification or search admission.
/// </remarks>
internal sealed class ShardStartupProbeHealthCheck(ShardStartupProbeOutcomes outcomes) : IHealthCheck
{
    private const string NotTested = "NotTested";
    private const string Shown = "Every required shard method showed strict handling and a lastUpdated replay.";
    private const string NotShown = "Not every required shard method has shown strict handling and a lastUpdated replay.";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // One read, so the verdict and the data describe one coherent snapshot.
        ShardStartupProbeSnapshot snapshot = outcomes.Current;
        var data = new Dictionary<string, object>(snapshot.Entries.Count);
        foreach (ShardStartupProbeEntry entry in snapshot.Entries)
        {
            data[$"{entry.Alias}/{entry.Method}"] = $"strict={entry.Strict?.ToString() ?? NotTested};replay={entry.Replay?.ToString() ?? NotTested}";
        }

        HealthCheckResult result = snapshot.IsReady
            ? HealthCheckResult.Healthy(Shown, data)
            : HealthCheckResult.Unhealthy(NotShown, data: data);
        return Task.FromResult(result);
    }
}
