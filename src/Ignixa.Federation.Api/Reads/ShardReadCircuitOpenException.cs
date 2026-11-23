using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Api.Reads;

/// <summary>
/// Thrown by an <see cref="IShardReadBinding"/> when the owning shard's circuit breaker is open and no backend call was
/// made. The proxied read answers 503 for it (design spec §8); every other owner failure is a
/// <see cref="Ignixa.Federation.Core.Execution.ShardFailureException"/>.
/// </summary>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "Hosts and the integration test project implement the binding that throws this type; the host project has no InternalsVisibleTo.")]
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Always names the owning shard whose circuit is open; a parameterless or message-only form would be an invalid instance.")]
public sealed class ShardReadCircuitOpenException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ShardReadCircuitOpenException"/> class.</summary>
    /// <param name="shard">The owning shard whose circuit is open.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shard"/> is <see langword="null"/>.</exception>
    public ShardReadCircuitOpenException(ShardId shard)
        : base("The owning shard's circuit is open.")
    {
        ArgumentNullException.ThrowIfNull(shard);
        Shard = shard;
    }

    /// <summary>Gets the owning shard whose circuit is open.</summary>
    public ShardId Shard { get; }
}
