using Microsoft.Extensions.Hosting;

namespace Ignixa.Federation.Shards.Capabilities.Refresh;

/// <summary>Runs the one <see cref="ShardCapabilityRefresher"/> for the life of the host.</summary>
/// <remarks>The host's stopping token is forwarded unchanged, so the host's shutdown joins the loop.</remarks>
internal sealed class ShardCapabilityRefreshWorker(ShardCapabilityRefresher refresher) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => refresher.RunAsync(stoppingToken);
}
