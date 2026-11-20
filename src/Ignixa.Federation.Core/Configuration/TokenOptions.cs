namespace Ignixa.Federation.Core.Configuration;

/// <summary>The <c>Federation:Tokens</c> section: continuation-token storage and lifetime (design spec §7.4).</summary>
public sealed class TokenOptions
{
    /// <summary>Gets or sets the largest snapshot carried inside the token; larger ones go to the distributed cache (design spec §7.4). Default 2048 bytes.</summary>
    public int MaxInlineTokenBytes { get; set; } = 2048;

    /// <summary>Gets or sets the sliding lifetime of a token (design spec §7.4, D5). Default 15 minutes.</summary>
    public TimeSpan SlidingLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Gets or sets the absolute lifetime of a token chain (design spec §7.4, D5). Default 60 minutes.</summary>
    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromMinutes(60);
}
