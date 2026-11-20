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
/// <param name="Redemption">How to redeem <paramref name="Target"/>.</param>
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
