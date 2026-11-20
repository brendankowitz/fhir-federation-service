namespace Ignixa.Federation.Core.Shards;

/// <summary>One backend page from a shard (design spec §7.3).</summary>
public sealed class ShardPage
{
    /// <summary>Initializes a new instance of the <see cref="ShardPage"/> class.</summary>
    /// <param name="entries">The page's entries in backend order; copied.</param>
    /// <param name="next">The cursor to the following page, or <see langword="null"/> when the shard has no more results.</param>
    /// <param name="total">The backend's <c>Bundle.total</c>, or <see langword="null"/> when it did not report one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="entries"/> contains <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="total"/> is negative.</exception>
    public ShardPage(IEnumerable<ShardEntry> entries, ShardCursor? next, long? total)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (total < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(total), total, "Total must not be negative.");
        }

        ShardEntry[] copy = [.. entries];
        if (Array.IndexOf(copy, null) >= 0)
        {
            throw new ArgumentException("Entries must not contain null.", nameof(entries));
        }

        Entries = Array.AsReadOnly(copy);
        Next = next;
        Total = total;
    }

    /// <summary>Gets the page's entries in backend order. May be empty even when <see cref="Next"/> is present.</summary>
    public IReadOnlyList<ShardEntry> Entries { get; }

    /// <summary>Gets the cursor to the following page, or <see langword="null"/> when the shard has no more results.</summary>
    public ShardCursor? Next { get; }

    /// <summary>
    /// Gets the backend's <c>Bundle.total</c>, or <see langword="null"/> when it did not report one. A missing total is
    /// unknown, not zero.
    /// </summary>
    public long? Total { get; }

    /// <summary>Gets a value indicating whether the shard has no more results; that is, <see cref="Next"/> is absent.</summary>
    public bool IsExhausted => Next is null;
}
