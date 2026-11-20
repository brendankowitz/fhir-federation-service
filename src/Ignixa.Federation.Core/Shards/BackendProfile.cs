namespace Ignixa.Federation.Core.Shards;

/// <summary>
/// The certified facts about one kind of FHIR backend that the federation relies on (design spec §9.2). Facts only;
/// the HTTP client in <c>Ignixa.Federation.Shards</c> acts on them. Shards name a profile in configuration.
/// </summary>
/// <remarks>
/// <para>
/// Strict handling (<c>Prefer: handling=strict</c>, §6.2) and replayable <c>next</c> links (§7.4) are admission
/// requirements for every backend, not per-profile options, so this record has no flags for them.
/// </para>
/// <para>
/// Every property is get-only, so a <see langword="with"/> expression can only copy a validated profile; the
/// constructor is the one place the invariants are checked.
/// </para>
/// </remarks>
/// <param name="Name">The profile name that shard configuration refers to. Not blank.</param>
/// <param name="SupportsPostSearch">Whether the backend accepts <c>POST [type]/_search</c>.</param>
/// <param name="MaxCount">The largest <c>_count</c> the backend honours. Greater than zero.</param>
/// <param name="NextLinkRedemption">
/// The redemption policy for streams the federation starts with POST; see <see cref="RedemptionFor"/>. A defined value,
/// and <see cref="NextLinkRedemption.RepostToSearch"/> only when <paramref name="SupportsPostSearch"/> is <see langword="true"/>.
/// </param>
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

    /// <summary>Gets a value indicating whether the backend accepts <c>POST [type]/_search</c>.</summary>
    public bool SupportsPostSearch { get; } = SupportsPostSearch;

    /// <summary>Gets the largest <c>_count</c> the backend honours. Always greater than zero.</summary>
    public int MaxCount { get; } = MaxCount > 0
        ? MaxCount
        : throw new ArgumentOutOfRangeException(nameof(MaxCount), MaxCount, "Max count must be greater than zero.");

    /// <summary>
    /// Gets the redemption policy for streams the federation starts with POST. Always a defined value, and never
    /// <see cref="NextLinkRedemption.RepostToSearch"/> unless <see cref="SupportsPostSearch"/>, because a re-post is a
    /// POST. A stream started with GET ignores it; use <see cref="RedemptionFor"/> rather than this policy for a cursor.
    /// </summary>
    public NextLinkRedemption NextLinkRedemption { get; } =
        !Enum.IsDefined(NextLinkRedemption)
            ? throw new ArgumentOutOfRangeException(nameof(NextLinkRedemption), NextLinkRedemption, "Undefined next-link redemption.")
        : NextLinkRedemption == NextLinkRedemption.RepostToSearch && !SupportsPostSearch
            ? throw new ArgumentException("RepostToSearch requires a profile that supports POST search.", nameof(NextLinkRedemption))
        : NextLinkRedemption;

    /// <summary>Gets the backend's certified <c>_lastUpdated</c> ordering, or <see langword="null"/> when it has none.</summary>
    public LastUpdatedOrder? LastUpdatedOrder { get; } = LastUpdatedOrder;

    /// <summary>
    /// Gets how to redeem the <c>next</c> links of a stream started with <paramref name="startedWith"/> (design spec §7.4):
    /// <see cref="NextLinkRedemption.RepostToSearch"/> only when this profile's policy is to re-post and the stream started
    /// with <see cref="ShardSearchMethod.PostSearch"/>; otherwise <see cref="NextLinkRedemption.FollowGet"/>.
    /// </summary>
    /// <param name="startedWith">How the federation sent the stream's first request.</param>
    /// <returns>The redemption for every cursor of that stream.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startedWith"/> is undefined.</exception>
    public NextLinkRedemption RedemptionFor(ShardSearchMethod startedWith)
    {
        if (!Enum.IsDefined(startedWith))
        {
            throw new ArgumentOutOfRangeException(nameof(startedWith), startedWith, "Undefined search method.");
        }

        return NextLinkRedemption == NextLinkRedemption.RepostToSearch && startedWith == ShardSearchMethod.PostSearch
            ? NextLinkRedemption.RepostToSearch
            : NextLinkRedemption.FollowGet;
    }
}
