using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution;

/// <summary>
/// A continuation replayed its shard input, but the result no longer contains the last emitted resource, so the page
/// cannot resume without a skip or a duplicate (design spec §7.4).
/// </summary>
/// <remarks>Its message names only the shard; it never holds a cursor target, query text or resource id.</remarks>
[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "A missing anchor is meaningless without the shard: a shard identity is required, so no constructor omits it.")]
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

    /// <summary>Gets the shard whose replayed page lacks the anchor.</summary>
    public ShardId Shard { get; }
}
