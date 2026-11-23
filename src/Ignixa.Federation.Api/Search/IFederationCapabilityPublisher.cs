using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Search;

namespace Ignixa.Federation.Api.Search;

/// <summary>
/// How a trusted host component installs, replaces or withdraws the capabilities view a configured search host serves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Trust.</b> The caller owns the evidence behind the view: it publishes only a complete, eligible view and never a
/// stale observation. Nothing from HTTP or configuration reaches this seam, and the host adds no authorization of its own
/// to what a published view allows.
/// </para>
/// <para>
/// <b>Effect.</b> A published view replaces the current one as a whole: the search, the capability statement and the
/// readiness check all follow it from the moment <see cref="Publish"/> returns. A request that has already started keeps the
/// view it began with; the next request sees the new one. Publishing <see langword="null"/> withdraws the view, so the
/// host is up but not ready until another is published. A withdrawal never falls back to a view registered at startup.
/// </para>
/// <para>
/// <b>Failure.</b> The replacement is built and validated completely before it is installed. If it cannot be built, the
/// call throws and the previous view, or its absence, stays in force: a rejected publication is not a withdrawal.
/// </para>
/// /// <para>
/// <b>Not reconfiguration.</b> Publishing changes only the capabilities view. The caller roles and grants, the token
/// authority, issuer and audience, signing, the public base and the <c>Federation</c> search options (limits, timeouts,
/// tokens, shards, <c>ProxyReads</c>) are all the host's startup settings: the options are copied from configuration when the
/// host is built and each publication builds its search from that copy, so changing or reloading configuration afterwards
/// has no effect on any publication. Invalid startup options do not stop a cold host from starting; they fail each
/// publication until the host is restarted.
/// </para>
/// <para>
/// Thread-safe. Calls are serialized and take effect in the order they acquire the lock, which is not a first-in first-out
/// guarantee for concurrent callers: the last view installed is the one from the call that took the lock last. A caller that
/// needs one view to win must finish its call before starting the next.
/// </para>
/// </remarks>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "Trusted host components outside this assembly resolve and call this seam, so it must be public; the integration tests could reach an internal type through InternalsVisibleTo and are not the reason.")]
public interface IFederationCapabilityPublisher
{
    /// <summary>Installs <paramref name="capabilities"/> as the served view, or withdraws the view when it is <see langword="null"/>.</summary>
    /// <param name="capabilities">The complete view to serve, or <see langword="null"/> to withdraw it.</param>
    /// <exception cref="InvalidOperationException">
    /// The search cannot be built from the view: no shard transport is registered, the startup <c>Federation</c> options are
    /// malformed or invalid, or its shards do not describe exactly the view's registry. The served view is unchanged.
    /// </exception>
    void Publish(SearchCapabilities? capabilities);
}
