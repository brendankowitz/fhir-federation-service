namespace Ignixa.Federation.Core.Shards;

/// <summary>
/// A backend's certified ordering of <c>_sort=_lastUpdated</c> results (design spec §7.3, §9.2): the timestamp precision it
/// orders by, and whether resources with equal timestamps keep a stable order across pages.
/// </summary>
/// <param name="Precision">The precision of the ordered timestamps. Greater than zero.</param>
/// <param name="StableTies">Whether ties keep a stable order across pages.</param>
public sealed record LastUpdatedOrder(TimeSpan Precision, bool StableTies)
{
    /// <summary>Gets the precision of the ordered timestamps. Always greater than zero.</summary>
    public TimeSpan Precision { get; } = Precision > TimeSpan.Zero
        ? Precision
        : throw new ArgumentOutOfRangeException(nameof(Precision), Precision, "Ordering precision must be greater than zero.");

    /// <summary>Gets a value indicating whether ties keep a stable order across pages.</summary>
    public bool StableTies { get; } = StableTies;
}
