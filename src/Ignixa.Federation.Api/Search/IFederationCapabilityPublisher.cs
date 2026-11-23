using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Search;

namespace Ignixa.Federation.Api.Search;

/// <summary>
/// How a trusted host component installs, replaces or withdraws the capabilities view a configured search host serves.
/// </summary>
/// <remarks>
/// <para>
/// The trusted in-process caller owns the evidence behind the view. It must publish only complete, eligible views and
/// never stale observations. There is no HTTP or configuration path for supplying a view. Publication does not change
/// caller authentication or grants.
/// </para>
/// <para>
/// Searches and qualified reads capture one generation in the request pipeline before authentication. Metadata captures
/// once in its handler. These requests keep the generation they captured even if a later call replaces or withdraws it.
/// Publishing <see langword="null"/> makes the host not ready without stopping it or restoring a startup view. A view is
/// necessary but not sufficient for readiness; the other readiness checks still apply.
/// </para>
/// <para>
/// The replacement is built and validated before it is installed, using startup settings. If it cannot be built, the
/// call throws and leaves the previous view in force. Invalid search options are reported when a warm view is built,
/// not while an unseeded host starts cold. Publishing a view does not apply configuration reloads.
/// </para>
/// <para>
/// Thread-safe. Calls are serialized, but concurrent calls have no guaranteed order. A source must order its own updates.
/// </para>
/// </remarks>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "Trusted host components outside this assembly resolve and call this seam.")]
public interface IFederationCapabilityPublisher
{
    /// <summary>Installs <paramref name="capabilities"/> as the served view, or withdraws the view when it is <see langword="null"/>.</summary>
    /// <param name="capabilities">The complete view to serve, or <see langword="null"/> to withdraw it.</param>
    /// <exception cref="InvalidOperationException">
    /// The generation could not be built, for example because there is no shard transport, startup options are invalid,
    /// or the configured shards do not describe the view's registry. The served view is unchanged.
    /// </exception>
    void Publish(SearchCapabilities? capabilities);
}
