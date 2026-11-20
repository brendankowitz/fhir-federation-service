using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution;

/// <summary>A shard whose results the page needs did not deliver them, so the page fails as a whole (design spec §8).</summary>
/// <remarks>
/// The executor always sets <see cref="Shard"/> and <see cref="Reason"/>. Its message names only the shard and the reason;
/// it never holds a cursor target, query text or resource bytes. The standard constructors exist for CA1032 and leave
/// <see cref="Shard"/> unset.
/// </remarks>
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

    /// <summary>Initializes a new instance of the <see cref="ShardFailureException"/> class with no shard.</summary>
    public ShardFailureException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ShardFailureException"/> class with no shard.</summary>
    /// <param name="message">The message.</param>
    public ShardFailureException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ShardFailureException"/> class with no shard.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ShardFailureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Gets the shard that failed, or <see langword="null"/> when a standard constructor created the exception.</summary>
    public ShardId? Shard { get; }

    /// <summary>
    /// Gets why the shard failed. It is <see cref="ShardFailureReason.Failed"/> when a standard constructor created the
    /// exception.
    /// </summary>
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
