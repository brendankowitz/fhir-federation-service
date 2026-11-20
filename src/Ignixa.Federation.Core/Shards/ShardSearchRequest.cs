using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;

namespace Ignixa.Federation.Core.Shards;

/// <summary>One search against one shard, for <see cref="IShardClient.SearchAsync"/> (design spec §7).</summary>
public sealed class ShardSearchRequest
{
    /// <summary>Initializes a new instance of the <see cref="ShardSearchRequest"/> class.</summary>
    /// <param name="shard">The shard to search.</param>
    /// <param name="type">The resource type to search.</param>
    /// <param name="parameters">The query occurrences to send, in order; copied.</param>
    /// <param name="pageSize">The <c>_count</c> to send; 0 asks for a count only.</param>
    /// <param name="method">How to send the search.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="parameters"/> contains <see langword="null"/> or a <c>_count</c> key, which <paramref name="pageSize"/> owns.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageSize"/> is negative or <paramref name="method"/> is undefined.</exception>
    public ShardSearchRequest(
        ShardId shard,
        ResourceType type,
        IEnumerable<ParameterOccurrence> parameters,
        int pageSize,
        ShardSearchMethod method)
    {
        ArgumentNullException.ThrowIfNull(shard);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentOutOfRangeException.ThrowIfNegative(pageSize);
        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method), method, "Undefined search method.");
        }

        ParameterOccurrence[] copy = [.. parameters];
        foreach (ParameterOccurrence occurrence in copy)
        {
            if (occurrence is null)
            {
                throw new ArgumentException("Parameters must not contain null.", nameof(parameters));
            }

            if (string.Equals(occurrence.Key, "_count", StringComparison.Ordinal))
            {
                throw new ArgumentException("Parameters must not contain _count; the page size sets it.", nameof(parameters));
            }
        }

        Shard = shard;
        Type = type;
        Parameters = Array.AsReadOnly(copy);
        PageSize = pageSize;
        Method = method;
    }

    /// <summary>Gets the shard to search.</summary>
    public ShardId Shard { get; }

    /// <summary>Gets the resource type to search.</summary>
    public ResourceType Type { get; }

    /// <summary>Gets the query occurrences to send, with their order, duplicates and encoded text preserved. Never contains <c>_count</c>.</summary>
    public IReadOnlyList<ParameterOccurrence> Parameters { get; }

    /// <summary>Gets the <c>_count</c> to send. Zero asks for a count only (design spec §7.2).</summary>
    public int PageSize { get; }

    /// <summary>Gets how to send the search.</summary>
    public ShardSearchMethod Method { get; }
}
