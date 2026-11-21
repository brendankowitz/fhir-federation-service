using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Capabilities;
using Ignixa.Federation.Shards.Http;

namespace Ignixa.Federation.Shards.Probes;

/// <summary>
/// The strict-handling part of the startup probe (design spec §6.2, §9.2): shows that one shard, for one advertised
/// searchable type and one wire method, answers an unadvertised ordinary parameter with HTTP 400.
/// </summary>
/// <remarks>
/// <para>
/// It uses the client's ordinary search transport (<c>Prefer: handling=strict</c>, the client's credential, deadline,
/// size limits, breaker and meter). It sends a bounded positive control, <c>_count=1</c> alone, and only if that returns
/// a valid page, the same search plus one parameter the shard does not advertise. One page is read and no <c>next</c>
/// link is followed.
/// </para>
/// <para>
/// Only a lone request answered with 400 is proof. A 200, any other status, a retry that ends
/// in 400 after another failure, a failure of the credential or meter, or a failed control is not. A credential outage
/// while acquiring the token is such a failure, so it is a negative result and the next method is still probed; the
/// credential provider's or meter's own unexpected exception still propagates. The probe proves one
/// method per call; GET evidence says nothing about POST, so a caller that offers both probes both. It does not admit
/// software or a profile, establish readiness, or test sorting or replay.
/// </para>
/// <para>
/// The probe fetches the shard's capability statement itself, so the type and the unadvertised parameter are checked
/// against the very shard that is probed. No query, body or exception is kept in the result.
/// </para>
/// </remarks>
public static class StrictHandlingProbe
{
    private const string ChallengeStem = "ignixa-strict-probe";
    private const string ChallengeValue = "1";
    private const int BadRequest = 400;

    /// <summary>Probes one shard, resource type and wire method.</summary>
    /// <param name="client">The shard's ordinary transport.</param>
    /// <param name="shard">The shard the client was built for.</param>
    /// <param name="type">The resource type to search; the shard must advertise <c>search-type</c> for it.</param>
    /// <param name="method">The one wire method to test. <see cref="ShardSearchMethod.PostSearch"/> requires the profile to offer POST search.</param>
    /// <param name="cancellationToken">Cancels the probe. Cancellation is thrown, never reported as an outcome.</param>
    /// <returns>What the probe established. Only <see cref="StrictHandlingProbeOutcome.Proven"/> is evidence.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="client"/> was not built for <paramref name="shard"/>. Nothing is sent.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="method"/> is undefined. Nothing is sent.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static async Task<StrictHandlingProbeResult> ProbeAsync(
        HttpShardClient client,
        ShardDescriptor shard,
        ResourceType type,
        ShardSearchMethod method,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(shard);
        ArgumentNullException.ThrowIfNull(type);
        if (!client.IsFor(shard))
        {
            throw new ArgumentException("The client was not built for this shard.", nameof(client));
        }

        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method), method, "Undefined search method.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        StrictHandlingProbeResult Result(StrictHandlingProbeOutcome outcome)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(shard.Id, type, method, outcome);
        }

        if (method == ShardSearchMethod.PostSearch && !shard.Profile.SupportsPostSearch)
        {
            return Result(StrictHandlingProbeOutcome.MethodNotOffered);
        }

        ShardCapabilityEvidence advertisement;
        try
        {
            advertisement = await client.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ShardCallException)
        {
            return Result(StrictHandlingProbeOutcome.AdvertisementUnavailable);
        }

        if (!advertisement.SearchableResources.TryGetValue(type, out ResourceSearchEvidence? resource))
        {
            return Result(StrictHandlingProbeOutcome.NotAdvertised);
        }

        try
        {
            await client.SearchForProbeAsync(Request(shard, type, method, []), witness: null, cancellationToken).ConfigureAwait(false);
        }
        catch (ShardCallException)
        {
            return Result(StrictHandlingProbeOutcome.ControlFailed);
        }

        var witness = new WireWitness();
        ParameterOccurrence[] challenge = [new(UnadvertisedCode(advertisement, resource), ChallengeValue)];
        try
        {
            await client.SearchForProbeAsync(Request(shard, type, method, challenge), witness, cancellationToken).ConfigureAwait(false);
            return Result(StrictHandlingProbeOutcome.ChallengeAccepted);
        }
        catch (ShardCallException) when (witness.WasOnlyAnsweredWith(BadRequest))
        {
            return Result(StrictHandlingProbeOutcome.Proven);
        }
        catch (ShardCallException)
        {
            return Result(StrictHandlingProbeOutcome.ChallengeNotProven);
        }
    }

    private static ShardSearchRequest Request(ShardDescriptor shard, ResourceType type, ShardSearchMethod method, ParameterOccurrence[] parameters) =>
        new(shard.Id, type, parameters, pageSize: 1, method);

    // Only the common parameters and the probed type's own are the shard's advertisement for this search; another type's
    // parameter would not make the code advertised here.
    private static string UnadvertisedCode(ShardCapabilityEvidence advertisement, ResourceSearchEvidence resource)
    {
        HashSet<string> advertised = new(StringComparer.OrdinalIgnoreCase);
        advertised.UnionWith(advertisement.CommonSearchParameters.Select(p => p.Code));
        advertised.UnionWith(resource.SearchParameters.Select(p => p.Code));

        string code = ChallengeStem;
        for (int suffix = 1; advertised.Contains(code); suffix++)
        {
            code = $"{ChallengeStem}-{suffix}";
        }

        return code;
    }
}
