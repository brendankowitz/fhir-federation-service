using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;

namespace Ignixa.Federation.Shards.Probes;

/// <summary>
/// The ordering-and-replay part of the startup probe (design spec §9.2): reads the first <c>_sort=_lastUpdated&amp;_count=1</c>
/// page of one shard and one searchable type, then redeems its <c>next</c> cursor twice, and reports what those three
/// exchanges showed.
/// </summary>
/// <remarks>
/// <para>
/// It uses the client's ordinary <see cref="HttpShardClient.SearchAsync"/> and <see cref="HttpShardClient.ContinueAsync"/>, so
/// the credential, deadline, size limits, breaker, retry policy and meter are the client's. The cursor is the one the client
/// returned, with the redemption its stream method and profile give it. The probe sends at most three logical exchanges, reads
/// no further page and writes nothing; a transport retry, if the client makes one, is the client's own.
/// </para>
/// <para>
/// <see cref="LastUpdatedReplayProbeOutcome.Observed"/> requires exactly one match on the first page and on each redemption, a
/// first-page <c>next</c>, <c>meta.lastUpdated</c> on the first-page and first-redeemed matches with the redeemed instant not
/// earlier (an equal instant is an observed tie only), two different resources across the boundary, and identical redeemed
/// entries. Entries are identical when their key, version, instant and exact resource bytes are equal. Exact bytes are a
/// conservative witness: a shard that writes equivalent JSON differently between redemptions is reported as differing. Totals
/// and the spelling of <c>next</c> links are not compared.
/// </para>
/// <para>
/// This is one sample of one stream shape. It does not certify ordering, precision, tie stability or replayability, does not
/// need the profile's <c>LastUpdatedOrder</c>, and does not fetch the shard's capability statement. A GET sample says nothing
/// about POST.
/// </para>
/// </remarks>
public static class LastUpdatedReplayProbe
{
    private static readonly ParameterOccurrence[] SortParameter = [new("_sort", "_lastUpdated")];

    /// <summary>Probes one shard, resource type and starting method.</summary>
    /// <param name="client">The shard's ordinary transport.</param>
    /// <param name="shard">The shard the client was built for.</param>
    /// <param name="type">The resource type to search. The caller owns whether the shard advertises it.</param>
    /// <param name="method">The wire method that starts the stream. <see cref="ShardSearchMethod.PostSearch"/> requires the profile to offer POST search.</param>
    /// <param name="cancellationToken">Cancels the probe. Cancellation is thrown, never reported as an outcome.</param>
    /// <returns>What the probe showed. Only <see cref="LastUpdatedReplayProbeOutcome.Observed"/> is evidence.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="client"/> was not built for <paramref name="shard"/>. Nothing is sent.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="method"/> is undefined. Nothing is sent.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <remarks>A failure that is not a <see cref="ShardCallException"/>, such as a credential provider's own bug, propagates unchanged.</remarks>
    public static async Task<LastUpdatedReplayProbeResult> ProbeAsync(
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

        NextLinkRedemption? redemption = null;

        LastUpdatedReplayProbeResult Result(LastUpdatedReplayProbeOutcome outcome)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(shard.Id, type, method, redemption, outcome);
        }

        if (method == ShardSearchMethod.PostSearch && !shard.Profile.SupportsPostSearch)
        {
            return Result(LastUpdatedReplayProbeOutcome.MethodNotOffered);
        }

        // Every sample is one atomic read: a refused or malformed exchange shows nothing, so there is no partial proof.
        ShardPage? start = await ReadAsync(
            () => client.SearchAsync(new ShardSearchRequest(shard.Id, type, SortParameter, pageSize: 1, method), cancellationToken)).ConfigureAwait(false);
        if (start is null)
        {
            return Result(LastUpdatedReplayProbeOutcome.ReadFailed);
        }

        redemption = start.Next?.Redemption;
        if (start.Entries.Count != 1 || start.Next is not { } cursor)
        {
            return Result(LastUpdatedReplayProbeOutcome.InsufficientRows);
        }

        ShardEntry first = start.Entries[0];
        if (first.LastUpdated is not { } firstInstant)
        {
            return Result(LastUpdatedReplayProbeOutcome.InstantMissing);
        }

        ShardPage? redeemed = await ReadAsync(() => client.ContinueAsync(cursor, cancellationToken)).ConfigureAwait(false);
        if (redeemed is null)
        {
            return Result(LastUpdatedReplayProbeOutcome.ReadFailed);
        }

        if (redeemed.Entries.Count != 1)
        {
            return Result(LastUpdatedReplayProbeOutcome.InsufficientRows);
        }

        ShardEntry next = redeemed.Entries[0];
        if (next.LastUpdated is not { } nextInstant)
        {
            return Result(LastUpdatedReplayProbeOutcome.InstantMissing);
        }

        if (next.Key == first.Key)
        {
            return Result(LastUpdatedReplayProbeOutcome.BoundaryNotDistinct);
        }

        if (nextInstant < firstInstant)
        {
            return Result(LastUpdatedReplayProbeOutcome.OrderNotMonotone);
        }

        ShardPage? replayed = await ReadAsync(() => client.ContinueAsync(cursor, cancellationToken)).ConfigureAwait(false);
        if (replayed is null)
        {
            return Result(LastUpdatedReplayProbeOutcome.ReadFailed);
        }

        if (replayed.Entries.Count != 1)
        {
            return Result(LastUpdatedReplayProbeOutcome.InsufficientRows);
        }

        return Result(Agree(next, replayed.Entries[0])
            ? LastUpdatedReplayProbeOutcome.Observed
            : LastUpdatedReplayProbeOutcome.ReplayDiffered);
    }

    // A refused or failed exchange is a classified outcome; anything else is an infrastructure fault and propagates.
    private static async Task<ShardPage?> ReadAsync(Func<Task<ShardPage>> exchange)
    {
        try
        {
            return await exchange().ConfigureAwait(false);
        }
        catch (ShardCallException)
        {
            return null;
        }
    }

    private static bool Agree(ShardEntry left, ShardEntry right) =>
        left.Key == right.Key
        && string.Equals(left.VersionId, right.VersionId, StringComparison.Ordinal)
        && left.LastUpdated == right.LastUpdated
        && left.Resource.Span.SequenceEqual(right.Resource.Span);
}
