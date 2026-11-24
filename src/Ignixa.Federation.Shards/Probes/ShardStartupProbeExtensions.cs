using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ignixa.Federation.Shards.Probes;

/// <summary>Registers the opt-in startup behavior probes with a Generic Host.</summary>
public static class ShardStartupProbeExtensions
{
    /// <summary>
    /// Probes strict handling and <c>_lastUpdated</c> cursor replay once at startup, and reports a <c>ready</c>-tagged health
    /// check named <c>shard-startup-probes</c> over what they showed.
    /// </summary>
    /// <param name="services">The host service collection, which already called <c>AddShardBackend</c>.</param>
    /// <param name="types">One resource type to probe for every registered shard. The mapping is copied at registration.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// <para><b>Work.</b> A one-shot hosted service uses the shared router's clients, one shard and method at a time: GET for
    /// every shard, and POST search where the shard's profile offers it. A method's replay probe runs only after its strict
    /// probe is proven. A negative outcome is recorded and the work continues; a failure the probes do not classify faults the
    /// hosted service under the host's <c>BackgroundServiceExceptionBehavior</c> and never marks a shard ready. The work adds no
    /// loop and no timeout of its own, and stopping the host cancels it and waits for it to end.</para>
    /// <para><b>Health.</b> Unhealthy until every required method has proven strict handling and shown a replay, otherwise
    /// healthy. It reads memory only and never calls a backend. This is behavioral evidence from one sample, not software
    /// identity, certification or search admission.</para>
    /// <para><b>Completeness.</b> The selection must name each registered shard exactly once; the host fails at startup, before
    /// any backend is contacted, if it does not.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A selected resource type is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The shared shard backend is not registered, or startup probes already are.</exception>
    public static IServiceCollection AddShardStartupProbes(
        this IServiceCollection services,
        IReadOnlyDictionary<ShardId, ResourceType> types)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(types);

        var selection = new Dictionary<ShardId, ResourceType>(types.Count);
        foreach ((ShardId shard, ResourceType type) in types)
        {
            selection.Add(shard, type ?? throw new ArgumentException("A startup probe resource type is null.", nameof(types)));
        }

        if (!HasRegistration<MultiShardHttpClient>(services))
        {
            throw new InvalidOperationException("Startup probes use the shared shard backend; call AddShardBackend first.");
        }

        if (HasRegistration<ShardStartupProbeOutcomes>(services))
        {
            throw new InvalidOperationException("Startup probes are already registered.");
        }

        services.AddSingleton(provider => new ShardStartupProbeOutcomes(provider.GetRequiredService<ShardRegistry>(), selection));
        services.AddHostedService<ShardStartupProbeWorker>();
        services.AddHealthChecks().AddCheck<ShardStartupProbeHealthCheck>("shard-startup-probes", tags: ["ready"]);
        return services;
    }

    private static bool HasRegistration<T>(IServiceCollection services) =>
        services.Any(descriptor => descriptor.ServiceType == typeof(T) && !descriptor.IsKeyedService);
}
