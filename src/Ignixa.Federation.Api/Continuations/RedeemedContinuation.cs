using System.Collections.Immutable;

namespace Ignixa.Federation.Api.Continuations;

/// <summary>
/// A successfully redeemed continuation: the snapshot to resume from, plus the authenticated binding, dependencies,
/// and original chain timing that a successor must carry forward unchanged.
/// </summary>
/// <remarks>
/// The protector creates this from authenticated state, never from caller-supplied scope.
/// The default <see cref="object.ToString"/> is
/// kept, so neither the snapshot nor the binding is rendered.
/// </remarks>
internal sealed class RedeemedContinuation
{
    internal RedeemedContinuation(
        ContinuationBinding binding,
        ContinuationDependencySet dependencies,
        long chainIssuedTicks,
        long chainDeadlineTicks,
        ImmutableArray<byte> snapshot)
    {
        Binding = binding;
        Dependencies = dependencies;
        ChainIssuedTicks = chainIssuedTicks;
        ChainDeadlineTicks = chainDeadlineTicks;
        Snapshot = snapshot;
    }

    /// <summary>
    /// Gets the trusted internal plan and replay bytes the token was issued with. A fresh copy per redemption, so
    /// repeat redemption of the same token always yields the same bytes.
    /// </summary>
    public ImmutableArray<byte> Snapshot { get; }

    /// <summary>Gets the caller the chain is bound to.</summary>
    internal ContinuationBinding Binding { get; }

    /// <summary>Gets the dependencies the chain was planned against.</summary>
    internal ContinuationDependencySet Dependencies { get; }

    /// <summary>Gets the UTC ticks of the chain's original issue.</summary>
    internal long ChainIssuedTicks { get; }

    /// <summary>Gets the absolute deadline set at the chain's original issue.</summary>
    internal long ChainDeadlineTicks { get; }
}
