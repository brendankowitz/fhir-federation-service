using System.Security.Cryptography.X509Certificates;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ignixa.Federation.Shards.Capabilities.Refresh;

/// <summary>Registers the shared shard transport and capability refresh with a Generic Host.</summary>
public static class ShardCapabilityRefreshExtensions
{
    /// <summary>Registers the host-owned shard router and runs capability refresh over its clients.</summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="registry">The validated registry shared by traffic and refresh.</param>
    /// <param name="credentials">The dedicated backend credential provider. The caller retains ownership.</param>
    /// <param name="callTimeout">The deadline for a backend call, including credential acquisition and body consumption.</param>
    /// <param name="maxResponseBytes">The maximum response body bytes read per backend call.</param>
    /// <param name="limits">One explicit search limit per registered shard. The mapping is copied at registration.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// Call once and do not replace the registry, router or refresher registrations. The host resolves the singleton
    /// router at startup, where its transport settings are validated, and disposes it at teardown. Request handlers
    /// resolve <see cref="MultiShardHttpClient"/> and borrow a view with <see cref="MultiShardHttpClient.ForRequest"/>;
    /// refresh uses the same clients without a meter.
    /// The refresh health check reports advertisements, not backend admission or verification.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A registry, router or refresher is already registered.</exception>
    public static IServiceCollection AddShardBackend(
        this IServiceCollection services,
        ShardRegistry registry,
        IShardCredentialProvider credentials,
        TimeSpan callTimeout,
        long maxResponseBytes,
        IReadOnlyDictionary<ShardId, ShardRequestLimits> limits) =>
        services.AddShardBackend(registry, credentials, callTimeout, maxResponseBytes, limits, trustedTestCertificate: null);

    internal static IServiceCollection AddShardBackend(
        this IServiceCollection services,
        ShardRegistry registry,
        IShardCredentialProvider credentials,
        TimeSpan callTimeout,
        long maxResponseBytes,
        IReadOnlyDictionary<ShardId, ShardRequestLimits> limits,
        X509Certificate2? trustedTestCertificate)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(limits);

        if (HasRegistration<ShardRegistry>(services) || HasRegistration<MultiShardHttpClient>(services) ||
            HasRegistration<ShardCapabilityRefresher>(services))
        {
            throw new InvalidOperationException("The shared shard backend owns the registry, router and capability refresh registrations.");
        }

        var boundLimits = new Dictionary<ShardId, ShardRequestLimits>(limits);
        services.AddSingleton(registry);
        services.AddSingleton(_ => new MultiShardHttpClient(
            registry, credentials, callTimeout, maxResponseBytes, boundLimits, meter: null, trustedTestCertificate));
        return RegisterRefresh(services, provider => provider.GetRequiredService<MultiShardHttpClient>().GetClient);
    }

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

        if (HasRegistration<RegistrationMarker>(services))
        {
            return services;
        }

        if (HasRegistration<ShardCapabilityRefresher>(services))
        {
            throw new InvalidOperationException("Capability refresh owns the host's ShardCapabilityRefresher registration.");
        }

        return RegisterRefresh(services, provider => shard => clientForShard(provider, shard));
    }

    private static IServiceCollection RegisterRefresh(
        IServiceCollection services,
        Func<IServiceProvider, Func<ShardDescriptor, HttpShardClient>> bindClients)
    {
        services.AddSingleton(new RegistrationMarker());
        services.AddSingleton(provider =>
        {
            Func<ShardDescriptor, HttpShardClient> clientForShard = bindClients(provider);
            return new ShardCapabilityRefresher(
                provider.GetRequiredService<ShardRegistry>(),
                (shard, cancellationToken) => Fetch(clientForShard, shard, cancellationToken),
                provider.GetService<TimeProvider>() ?? TimeProvider.System);
        });
        services.AddHostedService<ShardCapabilityRefreshWorker>();
        services.AddHealthChecks().AddCheck<ShardCapabilityHealthCheck>("shard-capabilities", tags: ["ready"]);
        return services;
    }

    private static Task<ShardCapabilityEvidence> Fetch(
        Func<ShardDescriptor, HttpShardClient> clientForShard,
        ShardDescriptor shard,
        CancellationToken cancellationToken)
    {
        HttpShardClient client = clientForShard(shard)
            ?? throw new InvalidOperationException("No metadata client was composed for a registered shard.");
        return client.IsFor(shard)
            ? client.GetCapabilitiesAsync(cancellationToken)
            : throw new InvalidOperationException("The metadata client was built for a different shard.");
    }

    private static bool HasRegistration<T>(IServiceCollection services) =>
        services.Any(descriptor => descriptor.ServiceType == typeof(T) && !descriptor.IsKeyedService);

    private sealed class RegistrationMarker;
}
