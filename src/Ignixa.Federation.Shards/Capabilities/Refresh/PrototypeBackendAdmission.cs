using System.Collections.Immutable;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Capabilities.Refresh;

/// <summary>
/// What <see cref="PrototypeBackendCatalog.TryAdmitAsync"/> admitted: the narrow Patient search view and the one resource
/// type a host may read, bound to the exact evidence the probes passed against.
/// </summary>
/// <remarks>
/// <para>Immutable. Admission is a local, opt-in prototype: the operator's choice of pinned profile attests the deployed
/// backend, and nothing here is a release certificate or an authorization. <see cref="ReadableResourceTypes"/> is a result
/// for the host to apply, not a restriction on the transport's own read.</para>
/// <para>It never changes after it is returned; <see cref="IsCurrent"/> says whether it still describes what the refresher
/// publishes.</para>
/// </remarks>
public sealed class PrototypeBackendAdmission
{
    private readonly ShardCapabilityRefresher _refresher;
    private readonly ImmutableArray<(ShardId Shard, ShardCapabilityEvidence Evidence)> _admitted;

    internal PrototypeBackendAdmission(
        ShardCapabilityRefresher refresher,
        ImmutableArray<(ShardId Shard, ShardCapabilityEvidence Evidence)> admitted,
        SearchCapabilities searchCapabilities,
        ImmutableArray<ResourceType> readableResourceTypes)
    {
        _refresher = refresher;
        _admitted = admitted;
        SearchCapabilities = searchCapabilities;
        ReadableResourceTypes = readableResourceTypes;
    }

    /// <summary>
    /// Gets the planner input: <c>Patient</c> alone, with only <c>_id</c> and <c>given</c>, and no includes or optional
    /// search proofs. Its registry is the refresher's.
    /// </summary>
    public SearchCapabilities SearchCapabilities { get; }

    /// <summary>Gets the resource types every shard advertised an unmodified <c>read</c> for: only <c>Patient</c>.</summary>
    public ImmutableArray<ResourceType> ReadableResourceTypes { get; }

    /// <summary>
    /// Reports whether the refresher still publishes, for every shard, the very evidence instances this admission was
    /// granted against. Sends nothing.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> once any shard's evidence has been replaced, even by a successful refetch with identical
    /// content, which needs a new admission. A failed refresh keeps the evidence, so this stays <see langword="true"/>.
    /// </returns>
    public bool IsCurrent() => IsCurrentIn(_refresher.Current);

    /// <summary>
    /// Runs <paramref name="install"/> once, only if this admission is still current, with no refresh publication able to
    /// interleave between that check and the end of the action.
    /// </summary>
    /// <param name="install">
    /// Swaps already prepared state in. It runs on the calling thread while refresh publication waits, so it must be a short,
    /// nonblocking, synchronous swap of state built beforehand: no probes, network or other I/O, view construction, request
    /// work, or awaiting. It must not re-enter this admission or the refresher, or wait for a refresh to publish: a refresh
    /// cannot publish until the action returns. The caller trusts it; nothing here rolls back what it did.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the action ran to completion. <see langword="false"/> if the evidence had already been
    /// replaced, in which case the action never runs. Evidence retained through a failed refresh stays eligible.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="install"/> is <see langword="null"/>.</exception>
    /// <exception cref="Exception">Whatever <paramref name="install"/> throws propagates unchanged; later refreshes and commits proceed.</exception>
    /// <remarks>
    /// Currency can change as soon as this returns: what holds is that no publication landed between the check and the
    /// action, not that the installed state stays current. <see cref="IsCurrent"/> remains a lock-free observation that
    /// guarantees nothing about what follows it.
    /// </remarks>
    public bool TryCommitIfCurrent(Action install)
    {
        ArgumentNullException.ThrowIfNull(install);
        return _refresher.TryCommitIfCurrent(IsCurrentIn, install);
    }

    private bool IsCurrentIn(ShardCapabilitySnapshot snapshot) =>
        _admitted.All(admitted => ReferenceEquals(snapshot.Shards[admitted.Shard].Evidence, admitted.Evidence));
}
