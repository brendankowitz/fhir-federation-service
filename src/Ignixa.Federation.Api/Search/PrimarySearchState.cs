using Ignixa.Federation.Api.Metadata;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ignixa.Federation.Api.Search;

/// <summary>
/// Whether a configured host has a capabilities view yet, and everything built from it, as one immutable bundle.
/// Also the <c>search-capabilities</c> readiness check: unhealthy while there is no view.
/// </summary>
/// <remarks>
/// A host with no <see cref="SearchCapabilities"/> is up but cold: every route that needs the view refuses before doing any
/// work. The view is the one registered at startup, built eagerly so a missing transport, invalid options or a registry
/// mismatch still fail startup, and afterwards whatever a trusted component publishes through
/// <see cref="IFederationCapabilityPublisher"/>; the latest publication is the only source, and a withdrawal does not fall
/// back to the registered view. The search and the capability statement are built from that one view, as a pair, before
/// they are installed, so they cannot describe different views and a failed build installs nothing. A request reads
/// <see cref="Current"/> once and uses only that bundle, so a publication never changes a request in flight.
/// <para>
/// Capture the raw search options at startup so publication cannot mix newly reloaded settings with the host's startup
/// settings. Binding and validation wait for a view; bad search options must not prevent a cold host from listening.
/// </para>
/// Readers do not take the writer lock. Manual <see cref="Publish"/> serializes its build and swap and uses unconditional
/// currency. A proof-backed source prepares outside its commit gate and owns the order of its installs and withdrawals.
/// </remarks>
internal sealed class PrimarySearchState : IHealthCheck, IFederationCapabilityPublisher
{
    /// <summary>The name of the readiness check.</summary>
    public const string HealthCheckName = "search-capabilities";

    /// <summary>The fixed diagnostics every cold refusal carries.</summary>
    public const string NotReady = "Federation capabilities are not ready.";

    private readonly IServiceProvider _services;
    private readonly SearchHostSettings _settings;
    private readonly KeyValuePair<string, string?>[] _federation;
    private readonly Lock _writer = new();
    private Ready? _current;

    private PrimarySearchState(IServiceProvider services, SearchHostSettings settings)
    {
        _services = services;
        _settings = settings;
        _federation = [.. services.GetRequiredService<IConfiguration>()
            .GetSection(FederationOptions.SectionName)
            .AsEnumerable(makePathsRelative: true)
            .Where(entry => entry.Value is not null)];
    }

    /// <summary>
    /// Gets the installed bundle if its evidence still holds, or <see langword="null"/> while the host has none or the evidence
    /// has turned false.
    /// </summary>
    /// <remarks>
    /// Reads the generation once and evaluates its <see cref="Ready.IsCurrent"/> once, so a caller must capture the result once
    /// per request and not re-evaluate it in flight. The predicate must be trusted, thread-safe, synchronous, non-throwing and
    /// free of I/O. A reader never clears the state; an invalid generation stays installed until replaced or withdrawn.
    /// </remarks>
    public Ready? Current => Volatile.Read(ref _current) is { } ready && ready.IsCurrent() ? ready : null;

    /// <summary>
    /// Builds a complete, coherent search and capability statement from <paramref name="capabilities"/> with the startup
    /// settings, without installing it, taking the writer lock or evaluating <paramref name="isCurrent"/>.
    /// </summary>
    /// <param name="capabilities">The view to build from.</param>
    /// <param name="isCurrent">The evidence that the view is still the one to serve; see <see cref="Current"/> for its contract.</param>
    /// <param name="readableTypes">
    /// The types with proven owner-qualified reads, or <see langword="null"/> for a static or manual view, whose searchable
    /// types are all readable. The read serves only searchable types in this set, frozen into the bundle.
    /// </param>
    /// <returns>The prepared bundle; <see cref="Install"/> makes it visible.</returns>
    /// <exception cref="InvalidOperationException">Search cannot be built (see <see cref="PrimarySearch.Create"/>); nothing is installed.</exception>
    internal Ready Prepare(SearchCapabilities capabilities, Func<bool> isCurrent, IReadOnlySet<ResourceType>? readableTypes = null)
    {
        // Bound per build from the startup copy, so a malformed value fails here rather than when the host starts.
        var options = new FederationOptions();
        using (var snapshot = new ConfigurationManager())
        {
            snapshot.AddInMemoryCollection(_federation);
            snapshot.Bind(options);
        }

        PrimarySearch search = PrimarySearch.Create(_services, _settings, options, capabilities, readableTypes);
        DateTimeOffset publishedAt = _services.GetRequiredService<TimeProvider>().GetUtcNow();
        byte[] statement = MetadataEndpoints.WriteStatement(_settings.PublicBase, publishedAt, capabilities.Resources, search.Reads);
        return new Ready(search, statement, isCurrent);
    }

    /// <summary>Swaps in a bundle prepared by this state, without building, blocking or checking evidence.</summary>
    /// <remarks>
    /// The sole proof-backed publisher must call this under its evidence commit gate, ordered with its own
    /// <c>Publish(null)</c> withdrawals. Do not run an independent manual publisher on that host.
    /// </remarks>
    internal void Install(Ready prepared) => Volatile.Write(ref _current, prepared);

    /// <summary>Builds the state, seeded from the registered view if there is one.</summary>
    /// <param name="services">The application services.</param>
    /// <param name="settings">The validated host settings.</param>
    /// <returns>The state, cold when no view is registered.</returns>
    /// <exception cref="InvalidOperationException">A view is registered but search cannot be built (see <see cref="PrimarySearch.Create"/>).</exception>
    public static PrimarySearchState Create(IServiceProvider services, SearchHostSettings settings)
    {
        var state = new PrimarySearchState(services, settings);
        state.Publish(services.GetService<SearchCapabilities>());
        return state;
    }

    /// <inheritdoc />
    public void Publish(SearchCapabilities? capabilities)
    {
        // A withdrawal must wait for an in-progress build so the build cannot install after the withdrawal returns.
        lock (_writer)
        {
            Volatile.Write(ref _current, capabilities is null ? null : Prepare(capabilities, static () => true));
        }
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(Current is not null ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy(NotReady));

    /// <summary>What a view makes available: the search and the capability statement bytes written from the same view.</summary>
    /// <param name="Search">The search planned against the view.</param>
    /// <param name="Statement">The serialized CapabilityStatement.</param>
    /// <param name="IsCurrent">The evidence predicate, evaluated by <see cref="Current"/>.</param>
    internal sealed record Ready(PrimarySearch Search, ReadOnlyMemory<byte> Statement, Func<bool> IsCurrent);
}
