using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ignixa.Federation.Shards.Capabilities.Refresh;

/// <summary>
/// Reports whether advertisements were fetched, from the refresher's published snapshot alone. It calls no backend and
/// claims nothing about profiles, credentials or whether a shard behaves as it advertises.
/// </summary>
internal sealed class ShardCapabilityHealthCheck(ShardCapabilityRefresher refresher) : IHealthCheck
{
    private const string NoShards = "No shards are registered.";
    private const string NotFetched = "Capability advertisements have not been fetched from every registered shard.";
    private const string Retained = "A capability advertisement is retained from before a failed refresh.";
    private const string Fetched = "Capability advertisements were fetched from every registered shard.";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // One read, so the verdict describes one coherent snapshot.
        ShardCapabilitySnapshot snapshot = refresher.Current;

        // A snapshot with no shards is vacuously complete; a service with nothing to federate is not ready.
        HealthCheckResult result = snapshot.Shards.Count == 0
            ? HealthCheckResult.Unhealthy(NoShards)
            : !snapshot.IsComplete
                ? HealthCheckResult.Unhealthy(NotFetched)
                : snapshot.IsDegraded
                    ? HealthCheckResult.Degraded(Retained)
                    : HealthCheckResult.Healthy(Fetched);
        return Task.FromResult(result);
    }
}
