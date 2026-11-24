using System.Collections.Immutable;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;

namespace Ignixa.Federation.Shards.Capabilities.Refresh;

/// <summary>
/// The two backend builds the local prototype is willing to federate, and the admission that binds a deployment to them.
/// This is an opt-in prototype, not a design spec §9.2 release certificate.
/// </summary>
/// <remarks>
/// <para><b>Profiles.</b> Both are GET only, follow <c>next</c> links, honor <c>_count</c> up to one, certify no
/// <c>_lastUpdated</c> order and no optional search behavior, and are limited to Patient <c>_id</c> and <c>given</c> search
/// plus qualified Patient read. There is no default: a shard uses one only when an operator names it.</para>
/// <para><b>Trust.</b> Naming a profile for a backend is the operator's attestation of what is deployed there. The
/// advertised software identity and any local build digest cannot attest a remote binary, so admission checks the
/// advertisement and the live behavior, nothing about the binary.</para>
/// </remarks>
public static class PrototypeBackendCatalog
{
    private static readonly ResourceType PatientType = ResourceType.Parse("Patient");

    private static readonly ImmutableArray<string> RequiredCodes = ["_id", "given"];

    private static readonly Pinned Ignixa = new(
        new BackendProfile("Ignixa-src-2f07ac88", SupportsPostSearch: false, MaxCount: 1, NextLinkRedemption.FollowGet, LastUpdatedOrder: null),
        SoftwareIdentityPolicy.Exact(
            "Ignixa FHIR Server",
            "0.6.83--no-branch-.22+Branch.-no-branch-.Sha.2f07ac88d655b3e8e87d41c39f6c2b4039b5a8d7.2f07ac88d655b3e8e87d41c39f6c2b4039b5a8d7"));

    private static readonly Pinned MicrosoftFhirServer = new(
        new BackendProfile("MicrosoftFhirServer-src-2a67597a", SupportsPostSearch: false, MaxCount: 1, NextLinkRedemption.FollowGet, LastUpdatedOrder: null),
        SoftwareIdentityPolicy.Exact("FHIR Server for Azure", "1.0.0"));

    /// <summary>
    /// Gets the pinned profiles to pass to <see cref="ShardRegistry.Create"/>. Admission recognizes these exact instances,
    /// not profiles that are equal in value or share a name.
    /// </summary>
    public static ImmutableArray<BackendProfile> Profiles { get; } = [Ignixa.Profile, MicrosoftFhirServer.Profile];

    /// <summary>
    /// Admits the refresher's current registry as a prototype deployment, or refuses.
    /// </summary>
    /// <param name="router">The real transport, built over the same registry instance as <paramref name="refresher"/>.</param>
    /// <param name="refresher">Supplies each shard's advertisement; the probes prove exactly the evidence held when this call starts.</param>
    /// <param name="cancellationToken">Cancels the admission. Cancellation is thrown, never reported as a refusal.</param>
    /// <returns>
    /// The admission, or <see langword="null"/> when any shard has no pinned profile or evidence transported for the
    /// router's exact descriptor, advertises another software identity, lacks searchable Patient with <c>read</c> or either selected
    /// code, fails the strict or <c>_lastUpdated</c>-replay probe, or had its evidence replaced meanwhile.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="router"/> is disposed.</exception>
    /// <remarks>
    /// The probes are those of <see cref="ShardCapabilityAdmission"/>, sent over the router's ordinary transport, one shard at
    /// a time, and they sample behavior only: sampling is not sort certification. No second metadata exchange is made. A
    /// failure other than a shard call failure, such as a credential provider's own bug, propagates unchanged.
    /// </remarks>
    public static async Task<PrototypeBackendAdmission?> TryAdmitAsync(
        MultiShardHttpClient router,
        ShardCapabilityRefresher refresher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(refresher);
        cancellationToken.ThrowIfCancellationRequested();

        ShardCapabilitySnapshot start = refresher.Current;
        var shards = new List<Admittable>(start.Descriptors.Count);
        foreach (ShardDescriptor descriptor in start.Descriptors)
        {
            if (Admittable.TryCreate(descriptor, router, start) is not { } shard)
            {
                return null;
            }

            shards.Add(shard);
        }

        // The projection is authoritative; asking it first only spares probes for a deployment it would refuse anyway.
        if (!start.TryProjectPatientSearchCapabilities(RequiredCodes, out SearchCapabilities? capabilities))
        {
            return null;
        }

        foreach (Admittable shard in shards)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(refresher.EvidenceFor(shard.Descriptor), shard.Evidence)
                || !await ShardCapabilityAdmission.RunProbesAsync(shard.Client, shard.Descriptor, PatientType, shard.Evidence, cancellationToken)
                    .ConfigureAwait(false))
            {
                return null;
            }
        }

        var admission = new PrototypeBackendAdmission(
            refresher,
            [.. shards.Select(s => (s.Descriptor.Id, s.Evidence))],
            capabilities,
            [PatientType]);
        cancellationToken.ThrowIfCancellationRequested();
        return admission.IsCurrent() ? admission : null;
    }

    private sealed record Pinned(BackendProfile Profile, SoftwareIdentityPolicy Identity);

    private sealed record Admittable(
        ShardDescriptor Descriptor,
        HttpShardClient Client,
        ShardCapabilityEvidence Evidence)
    {
        // Null unless this descriptor carries a pinned profile, has transported evidence, and advertises that
        // profile's software and an unmodified Patient read.
        internal static Admittable? TryCreate(ShardDescriptor descriptor, MultiShardHttpClient router, ShardCapabilitySnapshot snapshot)
        {
            Pinned? pinned = ReferenceEquals(descriptor.Profile, Ignixa.Profile) ? Ignixa
                : ReferenceEquals(descriptor.Profile, MicrosoftFhirServer.Profile) ? MicrosoftFhirServer
                : null;
            if (pinned is null
                || router.FindClient(descriptor) is not { } client
                || snapshot.Shards[descriptor.Id].Evidence is not { } evidence
                || !ReferenceEquals(evidence.TransportedFrom, descriptor)
                || pinned.Identity.Match(evidence) != SoftwareIdentityMatch.Matched
                || !evidence.SearchableResources.TryGetValue(PatientType, out ResourceSearchEvidence? patient)
                || !patient.AdvertisesRead)
            {
                return null;
            }

            return new Admittable(descriptor, client, evidence);
        }
    }
}
