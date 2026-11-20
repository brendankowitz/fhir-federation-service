namespace Ignixa.Federation.Core.Search;

/// <summary>The direction of a <c>_sort</c> on <c>_lastUpdated</c>, the only sort the federation merges.</summary>
public enum LastUpdatedSort
{
    /// <summary><c>_sort=_lastUpdated</c>: oldest first.</summary>
    Ascending,

    /// <summary><c>_sort=-_lastUpdated</c>: newest first.</summary>
    Descending,
}
