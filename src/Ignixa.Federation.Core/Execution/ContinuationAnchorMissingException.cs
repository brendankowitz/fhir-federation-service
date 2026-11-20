using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution;

/// <summary>
/// A continuation replayed its shard input, but the result no longer contains the last emitted resource, so the page
/// cannot resume without a skip or a duplicate (design spec §7.4).
/// </summary>
/// <remarks>
/// The executor always sets <see cref="Shard"/>. Its message names only the shard; it never holds a cursor target, query
/// text or resource id. The standard constructors exist for CA1032 and leave <see cref="Shard"/> unset.
/// </remarks>
public sealed class ContinuationAnchorMissingException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ContinuationAnchorMissingException"/> class.</summary>
    /// <param name="shard">The shard whose replayed page lacks the anchor.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shard"/> is <see langword="null"/>.</exception>
    public ContinuationAnchorMissingException(ShardId shard)
        : base($"The continuation anchor is missing from the replayed page of shard '{shard ?? throw new ArgumentNullException(nameof(shard))}'.")
    {
        Shard = shard;
    }

    /// <summary>Initializes a new instance of the <see cref="ContinuationAnchorMissingException"/> class with no shard.</summary>
    public ContinuationAnchorMissingException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ContinuationAnchorMissingException"/> class with no shard.</summary>
    /// <param name="message">The message.</param>
    public ContinuationAnchorMissingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ContinuationAnchorMissingException"/> class with no shard.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ContinuationAnchorMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Gets the shard whose replayed page lacks the anchor, or <see langword="null"/> when a standard constructor created
    /// the exception.
    /// </summary>
    public ShardId? Shard { get; }
}
