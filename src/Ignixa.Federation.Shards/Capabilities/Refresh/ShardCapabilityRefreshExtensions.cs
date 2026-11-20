using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ignixa.Federation.Shards.Capabilities.Refresh;

/// <summary>Registers the shard-side capability refresh with a Generic Host.</summary>
public static class ShardCapabilityRefreshExtensions
{
    /// <summary>
    /// Registers one <see cref="ShardCapabilityRefresher"/> singleton, the hosted service that starts and stops it, and a
    /// <c>ready</c>-tagged health check named <c>shard-capabilities</c> that reports its snapshot.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="clientForShard">
    /// Returns the host's configured client for a registered shard; it is called for every fetch with the root provider,
    /// so it should be a lookup, not a construction.
    /// </param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// <para><b>Composition.</b> The host must already register the validated <see cref="ShardRegistry"/>, and may register
    /// a <see cref="TimeProvider"/> (the system clock is used otherwise). Let this method register the refresher;
    /// applications read its <see cref="ShardCapabilityRefresher.Current"/> snapshot.</para>
    /// <para><b>Ownership.</b> The host creates, reuses and disposes the clients; this registration only borrows them and
    /// never disposes one. A client must be built for the very <see cref="ShardDescriptor"/> it is returned for. Any other
    /// client is refused with an <see cref="InvalidOperationException"/> before anything is sent, so evidence can never be
    /// published for the wrong shard; the refresher does not catch it, so the hosted service faults and the host's
    /// <c>BackgroundServiceExceptionBehavior</c> applies.</para>
    /// <para><b>Lifecycle.</b> Fetches use the bounded <see cref="HttpShardClient.GetCapabilitiesAsync"/> exchange.
    /// Stopping the host cancels the loop and any fetch in flight, and the host waits for the loop to end within its
    /// shutdown timeout.</para>
    /// <para><b>Health.</b> The check reads the published snapshot once and never calls a backend. It says only whether
    /// advertisements were fetched: <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy"/> until
    /// every shard has succeeded once, <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded"/>
    /// while any shard's latest fetch failed and its earlier advertisement is retained, otherwise
    /// <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy"/>. It does not verify a backend
    /// profile, a credential, or that a shard behaves as it advertises.</para>
    /// <para><b>Repeated use.</b> Idempotent: the first call wins, and later calls (with any delegate) add nothing, so
    /// there is never a second refresher, worker or check.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The host already registered a standalone refresher.</exception>
    public static IServiceCollection AddShardCapabilityRefresh(
        this IServiceCollection services,
        Func<IServiceProvider, ShardDescriptor, HttpShardClient> clientForShard)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clientForShard);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(RegistrationMarker)))
        {
            return services;
        }

        if (services.Any(descriptor => descriptor.ServiceType == typeof(ShardCapabilityRefresher) && !descriptor.IsKeyedService))
        {
            throw new InvalidOperationException("Capability refresh owns the host's ShardCapabilityRefresher registration.");
        }

        services.AddSingleton(new RegistrationMarker());
        services.AddSingleton(provider => new ShardCapabilityRefresher(
            provider.GetRequiredService<ShardRegistry>(),
            (shard, cancellationToken) => Fetch(provider, clientForShard, shard, cancellationToken),
            provider.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddHostedService<ShardCapabilityRefreshWorker>();
        services.AddHealthChecks().AddCheck<ShardCapabilityHealthCheck>("shard-capabilities", tags: ["ready"]);
        return services;
    }

    private static Task<ShardCapabilityEvidence> Fetch(
        IServiceProvider provider,
        Func<IServiceProvider, ShardDescriptor, HttpShardClient> clientForShard,
        ShardDescriptor shard,
        CancellationToken cancellationToken)
    {
        HttpShardClient client = clientForShard(provider, shard)
            ?? throw new InvalidOperationException("No metadata client was composed for a registered shard.");
        return client.IsFor(shard)
            ? client.GetCapabilitiesAsync(cancellationToken)
            : throw new InvalidOperationException("The metadata client was built for a different shard.");
    }

    private sealed class RegistrationMarker;
}
