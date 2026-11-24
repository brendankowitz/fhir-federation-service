using Ignixa.Federation.Shards.Http;
using Microsoft.Extensions.Hosting;

namespace Ignixa.Federation.Shards.Probes;

/// <summary>
/// Runs one startup pass over the shared router's clients. Each method uses at most six logical exchanges: metadata,
/// two strict searches, one sorted search and two redemptions. The existing transport bounds each exchange and its retries.
/// </summary>
/// <remarks>
/// A negative probe outcome is recorded and the next step runs. A failure the probes do not classify propagates, so the host's
/// <c>BackgroundServiceExceptionBehavior</c> applies and the unfinished steps stay not tested. The host's stopping token is
/// forwarded unchanged, so shutdown cancels the exchange in flight and joins this worker.
/// </remarks>
internal sealed class ShardStartupProbeWorker(MultiShardHttpClient router, ShardStartupProbeOutcomes outcomes) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (int index = 0; index < outcomes.Steps.Count; index++)
        {
            ShardStartupProbeStep step = outcomes.Steps[index];
            HttpShardClient client = router.GetClient(step.Shard);

            StrictHandlingProbeResult strict =
                await StrictHandlingProbe.ProbeAsync(client, step.Shard, step.Type, step.Method, stoppingToken).ConfigureAwait(false);
            outcomes.Record(index, strict.Outcome, replay: null);

            // Replay is only meaningful where this method just proved the chosen type is advertised and strictly handled.
            if (strict.IsProven)
            {
                LastUpdatedReplayProbeResult replay =
                    await LastUpdatedReplayProbe.ProbeAsync(client, step.Shard, step.Type, step.Method, stoppingToken).ConfigureAwait(false);
                outcomes.Record(index, strict.Outcome, replay.Outcome);
            }
        }
    }
}
