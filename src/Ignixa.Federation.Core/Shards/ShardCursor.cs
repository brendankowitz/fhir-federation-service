using System.Text;
using Ignixa.Federation.Core.Identity;

namespace Ignixa.Federation.Core.Shards;

/// <summary>
/// Where to resume a shard search: the backend's opaque <c>next</c> link and how to redeem it (design spec §7.4).
/// Core never interprets <see cref="Target"/>; it carries backend query text and may contain PHI, so
/// <see cref="ToString"/> leaves it out.
/// </summary>
/// <param name="Shard">The shard that issued the link.</param>
/// <param name="Type">The resource type being searched.</param>
/// <param name="Target">The backend's <c>next</c> link, verbatim. Not blank.</param>
/// <param name="Redemption">How to redeem <paramref name="Target"/>; <see cref="Next"/> derives it from the stream's starting method.</param>
public sealed record ShardCursor(ShardId Shard, ResourceType Type, string Target, NextLinkRedemption Redemption)
{
    /// <summary>Gets the shard that issued the link.</summary>
    public ShardId Shard { get; } = Shard ?? throw new ArgumentNullException(nameof(Shard));

    /// <summary>Gets the resource type being searched.</summary>
    public ResourceType Type { get; } = Type ?? throw new ArgumentNullException(nameof(Type));

    /// <summary>Gets the backend's <c>next</c> link, verbatim. Never blank; never logged.</summary>
    public string Target { get; } = string.IsNullOrWhiteSpace(Target)
        ? throw new ArgumentException("Cursor target must not be blank.", nameof(Target))
        : Target;

    /// <summary>Gets how to redeem <see cref="Target"/>. Always a defined value.</summary>
    public NextLinkRedemption Redemption { get; } = Enum.IsDefined(Redemption)
        ? Redemption
        : throw new ArgumentOutOfRangeException(nameof(Redemption), Redemption, "Undefined next-link redemption.");

    /// <summary>
    /// Creates the cursor for a backend <c>next</c> link in the stream that <paramref name="request"/> started, with the
    /// redemption <see cref="BackendProfile.RedemptionFor"/> gives for the request's method.
    /// </summary>
    /// <param name="request">The request that started the stream; it supplies the shard, type and starting method.</param>
    /// <param name="profile">The profile of the shard that issued the link.</param>
    /// <param name="target">The backend's <c>next</c> link, verbatim. Not blank.</param>
    /// <returns>The cursor.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> or <paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="target"/> is blank.</exception>
    public static ShardCursor Next(ShardSearchRequest request, BackendProfile profile, string target)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profile);
        return new ShardCursor(request.Shard, request.Type, target, profile.RedemptionFor(request.Method));
    }

    /// <summary>Describes the cursor without its <see cref="Target"/>.</summary>
    /// <returns>The shard, type and redemption.</returns>
    public override string ToString()
    {
        return new StringBuilder()
            .Append("ShardCursor { Shard = ").Append(Shard)
            .Append(", Type = ").Append(Type)
            .Append(", Redemption = ").Append(Redemption)
            .Append(" }")
            .ToString();
    }
}
