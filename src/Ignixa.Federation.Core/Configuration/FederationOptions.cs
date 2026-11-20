namespace Ignixa.Federation.Core.Configuration;

/// <summary>
/// The <c>Federation</c> configuration section (design spec §4.2). A mutable bag for the configuration binder;
/// <see cref="FederationOptionsValidator"/> checks it, and <see cref="Shards.ShardRegistry"/> turns the shards into a validated model.
/// </summary>
public sealed class FederationOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Federation";

    /// <summary>Gets the shards, in registry order; position is the shard ordinal (design spec §4.2).</summary>
    public IList<ShardOptions> Shards { get; } = new List<ShardOptions>();

    /// <summary>
    /// Gets or sets a value indicating whether the federation-proxied read route <c>[base]/shards/{alias}/[type]/[id]</c>
    /// is served (design spec §5.1). Defaults to <see langword="true"/>.
    /// </summary>
    public bool ProxyReads { get; set; } = true;

    /// <summary>Gets how chains and <c>_has</c> are resolved (design spec §7.5).</summary>
    public ChainResolutionOptions ChainResolution { get; } = new();

    /// <summary>Gets the size and cost limits (design spec §7).</summary>
    public LimitOptions Limits { get; } = new();

    /// <summary>Gets the deadlines (design spec §8).</summary>
    public TimeoutOptions Timeouts { get; } = new();

    /// <summary>Gets the continuation-token settings (design spec §7.4).</summary>
    public TokenOptions Tokens { get; } = new();
}
