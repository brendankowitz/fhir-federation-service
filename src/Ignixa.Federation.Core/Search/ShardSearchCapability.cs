using System.Collections.ObjectModel;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Search;

/// <summary>One registered shard's explicit search capability: what it advertises and what it is proven to answer exactly.</summary>
public sealed class ShardSearchCapability
{
    /// <summary>Initializes a new instance of the <see cref="ShardSearchCapability"/> class.</summary>
    /// <param name="shard">The shard.</param>
    /// <param name="resources">The advertised resource types; at least one, each once.</param>
    /// <param name="certification">The shard's proofs.</param>
    /// <param name="version">The shard's advertised FHIR version, exactly as received.</param>
    /// <exception cref="ArgumentException"><paramref name="resources"/> is empty or repeats a resource type.</exception>
    public ShardSearchCapability(ShardId shard, IEnumerable<ResourceSearchSupport> resources, SearchCertification certification, string version)
    {
        ArgumentNullException.ThrowIfNull(shard);
        ArgumentNullException.ThrowIfNull(certification);
        ArgumentNullException.ThrowIfNull(version);
        ResourceSearchSupport[] advertised = [.. resources];
        if (advertised.Length == 0)
        {
            throw new ArgumentException("A shard capability advertises at least one resource type.", nameof(resources));
        }

        if (advertised.DistinctBy(r => r.Type).Count() != advertised.Length)
        {
            throw new ArgumentException("A shard capability advertises each resource type once.", nameof(resources));
        }

        Shard = shard;
        Version = version;
        Resources = new ReadOnlyCollection<ResourceSearchSupport>(advertised);
        Certification = certification;
    }

    /// <summary>Gets the shard.</summary>
    public ShardId Shard { get; }

    /// <summary>Gets the shard's advertised FHIR version.</summary>
    public string Version { get; }

    /// <summary>Gets the advertised resource types.</summary>
    public IReadOnlyList<ResourceSearchSupport> Resources { get; }

    /// <summary>Gets the shard's proofs.</summary>
    public SearchCertification Certification { get; }
}
