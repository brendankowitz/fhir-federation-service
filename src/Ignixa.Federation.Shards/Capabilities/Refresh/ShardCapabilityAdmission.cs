using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;
using Ignixa.Federation.Shards.Probes;

namespace Ignixa.Federation.Shards.Capabilities.Refresh;

/// <summary>
/// Proves a shard's current advertisement against the strict-handling and <c>_lastUpdated</c>-replay probes and, only if
/// every probe passes, returns a <see cref="ShardCapabilityProof"/> for that exact advertisement.
/// </summary>
/// <remarks>
/// <para>
/// <b>Trust.</b> The caller supplies the identity policy and <see cref="SearchCertification"/> trusted for the shard's
/// profile. Admission checks the advertised identity but does not independently verify the supplied certification. The
/// strict probe reads first pages only; the ordering probe reads the sorted first page and redeems that page's cursor twice.
/// These samples say nothing about other types, parameters, sort orders or page sizes.
/// </para>
/// <para>
/// <b>Exact evidence.</b> The advertisement is read once from the refresher before any probe and is the only one used:
/// the strict probe does not fetch metadata itself here. It must have been fetched from this very descriptor by the
/// shard transport, so evidence parsed from bytes, or fetched for another shard, is refused. After the probes the
/// refresher's evidence must still be the same instance. A failed refresh can retain that instance; a successful refetch
/// produces a new instance and requires its own proof, even if its content is identical.
/// </para>
/// <para>
/// <b>Methods.</b> GET is always probed; POST search is probed as well only when the shard profile offers it. A method
/// passes only when the strict probe is <see cref="StrictHandlingProbeOutcome.Proven"/> and the replay probe is
/// <see cref="LastUpdatedReplayProbeOutcome.Observed"/>. The first method that does not pass ends the admission.
/// </para>
/// <para>
/// <b>Effects.</b> Nothing is published, retained or logged: the refresher, its snapshot and the registry are untouched,
/// and no query, body or credential is recorded. Probe traffic goes through the client's ordinary transport, so it is
/// metered and uses the client's credential, deadline and breaker.
/// </para>
/// </remarks>
public static class ShardCapabilityAdmission
{
    /// <summary>Proves <paramref name="shard"/>'s currently published advertisement, or refuses.</summary>
    /// <param name="client">The shard's ordinary transport.</param>
    /// <param name="shard">The registered shard the client was built for.</param>
    /// <param name="refresher">Supplies the advertisement to prove, which must have been fetched from <paramref name="shard"/>.</param>
    /// <param name="probeType">The resource type to probe; the advertisement must offer <c>search-type</c> for it.</param>
    /// <param name="identity">The software identity the advertisement must satisfy.</param>
    /// <param name="certification">What the caller has proven the shard answers exactly; carried into the proof unchanged.</param>
    /// <param name="cancellationToken">Cancels the admission. Cancellation is thrown, never reported as a refusal.</param>
    /// <returns>
    /// A proof bound to <paramref name="shard"/> and the exact evidence instance probed, or <see langword="null"/> when
    /// there is no evidence, it is not from this descriptor, the identity does not match, the type is not advertised, any
    /// probe fails, or the evidence was replaced while probing.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="client"/> was not built for <paramref name="shard"/>. Nothing is sent.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <remarks>A failure that is not a shard call failure, such as a credential or meter provider's own bug, propagates unchanged.</remarks>
    public static async Task<ShardCapabilityProof?> TryProveAsync(
        HttpShardClient client,
        ShardDescriptor shard,
        ShardCapabilityRefresher refresher,
        ResourceType probeType,
        SoftwareIdentityPolicy identity,
        SearchCertification certification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(shard);
        ArgumentNullException.ThrowIfNull(refresher);
        ArgumentNullException.ThrowIfNull(probeType);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(certification);
        if (!client.IsFor(shard))
        {
            throw new ArgumentException("The client was not built for this shard.", nameof(client));
        }

        cancellationToken.ThrowIfCancellationRequested();

        ShardCapabilityEvidence? evidence = refresher.EvidenceFor(shard);
        if (evidence is null
            || !ReferenceEquals(evidence.TransportedFrom, shard)
            || identity.Match(evidence) != SoftwareIdentityMatch.Matched
            || !evidence.SearchableResources.ContainsKey(probeType))
        {
            return null;
        }

        foreach (ShardSearchMethod method in MethodsOffered(shard))
        {
            StrictHandlingProbeResult strict = await StrictHandlingProbe
                .ProbeAdvertisedAsync(client, shard, probeType, method, evidence, cancellationToken)
                .ConfigureAwait(false);
            if (!strict.IsProven)
            {
                return null;
            }

            LastUpdatedReplayProbeResult replay = await LastUpdatedReplayProbe
                .ProbeAsync(client, shard, probeType, method, cancellationToken)
                .ConfigureAwait(false);
            if (!replay.IsObserved)
            {
                return null;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ReferenceEquals(refresher.EvidenceFor(shard), evidence)
            ? new ShardCapabilityProof(shard, evidence, certification)
            : null;
    }

    private static IEnumerable<ShardSearchMethod> MethodsOffered(ShardDescriptor shard)
    {
        yield return ShardSearchMethod.Get;
        if (shard.Profile.SupportsPostSearch)
        {
            yield return ShardSearchMethod.PostSearch;
        }
    }
}
