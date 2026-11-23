using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Api.Reads;

/// <summary>The outcome of <see cref="IShardReadBinding.ReadAsync"/>: the resource, or that the owner has none (404) or deleted it (410).</summary>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "Hosts and the integration test project implement the binding that returns this type; the host project has no InternalsVisibleTo.")]
public sealed class ShardReadResult
{
    private ShardReadResult(ShardEntry? entry) => Entry = entry;

    /// <summary>Gets the result for a resource the owner does not have.</summary>
    public static ShardReadResult NotFound { get; } = new(null);

    /// <summary>Gets the result for a resource the owner reports deleted.</summary>
    public static ShardReadResult Gone { get; } = new(null);

    /// <summary>Gets the entry of a found resource; <see langword="null"/> for <see cref="NotFound"/> and <see cref="Gone"/>.</summary>
    internal ShardEntry? Entry { get; }

    /// <summary>Reports the resource as read from its owner.</summary>
    /// <param name="entry">The resource with the owner's exact bytes; its key must be the key that was read.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    public static ShardReadResult Found(ShardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new ShardReadResult(entry);
    }
}
