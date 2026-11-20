using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution;

/// <summary>A shard whose results the page needs did not deliver them, so the page fails as a whole (design spec §8).</summary>
/// <remarks>
/// Its message names only the shard and the reason; it never holds a cursor target, query text or resource bytes.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "A shard failure is meaningless without the shard: a shard identity is required, so no constructor omits it.")]
public sealed class ShardFailureException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ShardFailureException"/> class.</summary>
    /// <param name="shard">The shard that failed.</param>
    /// <param name="reason">Why it failed.</param>
    /// <param name="innerException">The client's exception, if the client threw.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shard"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is undefined.</exception>
    public ShardFailureException(ShardId shard, ShardFailureReason reason, Exception? innerException)
        : base(Describe(shard, reason), innerException)
    {
        Shard = shard;
        Reason = reason;
    }

    /// <summary>Gets the shard that failed.</summary>
    public ShardId Shard { get; }

    /// <summary>Gets why the shard failed.</summary>
    public ShardFailureReason Reason { get; }

    private static string Describe(ShardId shard, ShardFailureReason reason)
    {
        ArgumentNullException.ThrowIfNull(shard);
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Undefined shard failure reason.");
        }

        return $"Shard '{shard}' failed: {reason}.";
    }
}
