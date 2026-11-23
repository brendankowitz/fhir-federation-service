using Ignixa.Federation.Api.Metadata;
using Ignixa.Federation.Core.Search;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ignixa.Federation.Api.Search;

/// <summary>
/// Whether a configured host has a capabilities view yet, and everything built from it, as one immutable bundle.
/// Also the <c>search-capabilities</c> readiness check: unhealthy while there is no view.
/// </summary>
/// <remarks>
/// A host with no registered <see cref="SearchCapabilities"/> is up but cold: nothing is resolved beyond the settings, and
/// every route that needs the view refuses before doing any work. A registered view is built eagerly, so a missing
/// transport, invalid options or a registry mismatch still fail startup. The search and the capability statement are built
/// from that one view, so they cannot describe different views. A request reads <see cref="Current"/> once and uses only
/// that bundle. Thread-safe: immutable after construction.
/// </remarks>
internal sealed class PrimarySearchState : IHealthCheck
{
    /// <summary>The name of the readiness check.</summary>
    public const string HealthCheckName = "search-capabilities";

    /// <summary>The fixed diagnostics every cold refusal carries.</summary>
    public const string NotReady = "Federation capabilities are not ready.";

    private PrimarySearchState(Ready? current) => Current = current;

    /// <summary>Gets the bundle built from the view, or <see langword="null"/> while the host has no view.</summary>
    public Ready? Current { get; }

    /// <summary>Builds the state from the registered view, if any.</summary>
    /// <param name="services">The application services.</param>
    /// <param name="settings">The validated host settings.</param>
    /// <returns>The state, cold when no view is registered.</returns>
    /// <exception cref="InvalidOperationException">A view is registered but search cannot be built (see <see cref="PrimarySearch.Create"/>).</exception>
    public static PrimarySearchState Create(IServiceProvider services, SearchHostSettings settings)
    {
        if (services.GetService<SearchCapabilities>() is not { } capabilities)
        {
            return new PrimarySearchState(null);
        }

        PrimarySearch search = PrimarySearch.Create(services, settings, capabilities);
        DateTimeOffset publishedAt = services.GetRequiredService<TimeProvider>().GetUtcNow();
        byte[] statement = MetadataEndpoints.WriteStatement(settings.PublicBase, publishedAt, capabilities.Resources, search.Reads.Enabled);
        return new PrimarySearchState(new Ready(search, statement));
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(Current is not null ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy(NotReady));

    /// <summary>What a view makes available: the search and the capability statement bytes written from the same view.</summary>
    /// <param name="Search">The search planned against the view.</param>
    /// <param name="Statement">The serialized CapabilityStatement.</param>
    internal sealed record Ready(PrimarySearch Search, ReadOnlyMemory<byte> Statement);
}
