using System.Diagnostics.CodeAnalysis;

namespace Ignixa.Federation.Core.Configuration;

/// <summary>One entry of <c>Federation:Shards</c> as configured (design spec §4.2). Raw text; <see cref="Shards.ShardRegistry"/> validates it.</summary>
public sealed class ShardOptions
{
    /// <summary>Gets or sets the stable shard id (design spec §4.2).</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the short, URL-safe alias used in proxied-read routes, the <c>source-shard</c> extension and logs (design spec §4.2, §11).</summary>
    public string? Alias { get; set; }

    /// <summary>Gets or sets the client-visible https base URL that forms <c>fullUrl</c> (design spec §4.2, §11).</summary>
    [SuppressMessage(
        "Design",
        "CA1056:URI-like properties should not be strings",
        Justification = "Raw configuration text; ShardRegistry parses and canonicalises it so it can report a config-path error.")]
    public string? BaseUrl { get; set; }

    /// <summary>Gets or sets the name of the backend profile the shard is certified against (design spec §9.2).</summary>
    public string? Profile { get; set; }

    /// <summary>Gets or sets the federation's credential for this shard (design spec §10.2). Required.</summary>
    public AuthOptions? Auth { get; set; }

    /// <summary>Gets the other https base forms the shard is known by, such as a custom domain (design spec §4.2, §10.2).</summary>
    public IList<string> LinkBaseAliases { get; } = new List<string>();
}
