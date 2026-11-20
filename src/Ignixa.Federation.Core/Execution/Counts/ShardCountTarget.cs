using System.Collections.Frozen;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.Counts;

/// <summary>
/// What one registered shard contributes to a federated count: a count-only search, or nothing because the shard is
/// proven to hold no match (for example, a chain whose join found no target on it).
/// </summary>
/// <remarks>Immutable; safe to share across threads.</remarks>
public sealed class ShardCountTarget
{
    private ShardCountTarget(ShardId shard, ShardSearchMethod method, FrozenDictionary<int, string>? replacements)
    {
        Shard = shard;
        Method = method;
        Replacements = replacements;
    }

    /// <summary>Gets the shard.</summary>
    public ShardId Shard { get; }

    internal ShardSearchMethod Method { get; }

    /// <summary>Gets the shard's resolved replacements, or <see langword="null"/> when the shard is proven empty.</summary>
    internal FrozenDictionary<int, string>? Replacements { get; }

    /// <summary>Counts the plan's matches on <paramref name="shard"/>.</summary>
    /// <param name="shard">The shard.</param>
    /// <param name="method">How the shard is searched.</param>
    /// <param name="replacements">
    /// This shard's decoded value for each occurrence the plan replaces, keyed by occurrence ordinal; empty when the plan
    /// replaces none. Copied.
    /// </param>
    /// <returns>The target.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shard"/> or <paramref name="replacements"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="method"/> is undefined.</exception>
    public static ShardCountTarget Search(ShardId shard, ShardSearchMethod method, IReadOnlyDictionary<int, string> replacements)
    {
        ArgumentNullException.ThrowIfNull(shard);
        ArgumentNullException.ThrowIfNull(replacements);
        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method), method, "Undefined search method.");
        }

        return new ShardCountTarget(shard, method, replacements.ToFrozenDictionary());
    }

    /// <summary>Counts zero for <paramref name="shard"/> without calling it, because it is proven to hold no match.</summary>
    /// <param name="shard">The shard.</param>
    /// <returns>The target.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shard"/> is <see langword="null"/>.</exception>
    public static ShardCountTarget ProvenEmpty(ShardId shard)
    {
        ArgumentNullException.ThrowIfNull(shard);
        return new ShardCountTarget(shard, default, null);
    }
}
