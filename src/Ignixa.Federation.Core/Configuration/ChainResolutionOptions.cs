namespace Ignixa.Federation.Core.Configuration;

/// <summary>The <c>Federation:ChainResolution</c> section (design spec §7.5).</summary>
public sealed class ChainResolutionOptions
{
    /// <summary>
    /// Gets or sets the mode for chain paths not listed in <see cref="Passthrough"/>. Must be
    /// <see cref="ChainResolutionMode.Distributed"/> in v1; Passthrough is chosen per path (design spec §7.5).
    /// </summary>
    public ChainResolutionMode Default { get; set; } = ChainResolutionMode.Distributed;

    /// <summary>
    /// Gets the <c>SourceType.referenceParameter:TargetType</c> paths, such as <c>Observation.subject:Patient</c>, that the
    /// operator asserts are shard-local and so resolve in Passthrough mode (design spec §7.5).
    /// </summary>
    public IList<string> Passthrough { get; } = new List<string>();
}
