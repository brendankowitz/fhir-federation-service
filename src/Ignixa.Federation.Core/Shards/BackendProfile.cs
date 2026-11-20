namespace Ignixa.Federation.Core.Shards;

/// <summary>
/// The certified facts about one kind of FHIR backend that the federation relies on (design spec §9.2). Facts only;
/// the HTTP client in <c>Ignixa.Federation.Shards</c> acts on them. Shards name a profile in configuration.
/// </summary>
/// <remarks>
/// Strict handling (<c>Prefer: handling=strict</c>, §6.2) and replayable <c>next</c> links (§7.4) are admission
/// requirements for every backend, not per-profile options, so this record has no flags for them.
/// </remarks>
/// <param name="Name">The profile name that shard configuration refers to. Not blank.</param>
/// <param name="SupportsPostSearch">Whether the backend accepts <c>POST [type]/_search</c>.</param>
/// <param name="MaxCount">The largest <c>_count</c> the backend honours. Greater than zero.</param>
/// <param name="NextLinkRedemption">How the backend's <c>next</c> links are redeemed.</param>
/// <param name="LastUpdatedOrder">The backend's certified <c>_lastUpdated</c> ordering, or <see langword="null"/> when it has none.</param>
public sealed record BackendProfile(
    string Name,
    bool SupportsPostSearch,
    int MaxCount,
    NextLinkRedemption NextLinkRedemption,
    LastUpdatedOrder? LastUpdatedOrder)
{
    /// <summary>Gets the profile name that shard configuration refers to. Never blank.</summary>
    public string Name { get; } = string.IsNullOrWhiteSpace(Name)
        ? throw new ArgumentException("Profile name must not be blank.", nameof(Name))
        : Name;

    /// <summary>Gets the largest <c>_count</c> the backend honours. Always greater than zero.</summary>
    public int MaxCount { get; } = MaxCount > 0
        ? MaxCount
        : throw new ArgumentOutOfRangeException(nameof(MaxCount), MaxCount, "Max count must be greater than zero.");

    /// <summary>Gets how the backend's <c>next</c> links are redeemed. Always a defined value.</summary>
    public NextLinkRedemption NextLinkRedemption { get; } = Enum.IsDefined(NextLinkRedemption)
        ? NextLinkRedemption
        : throw new ArgumentOutOfRangeException(nameof(NextLinkRedemption), NextLinkRedemption, "Undefined next-link redemption.");
}
