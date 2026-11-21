using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Capabilities.Refresh;

/// <summary>
/// Search proof the caller vouches for, bound to one exact shard descriptor and one exact advertisement instance.
/// </summary>
/// <remarks>
/// <para>The binding is by reference, never by shape: a descriptor from another registry, or an advertisement that merely
/// reads the same, does not match. A refetch that returns a new advertisement instance needs a new proof even when every
/// field is unchanged; evidence retained through a failed fetch is the same instance and keeps its proof.</para>
/// <para>The caller is accountable for the proof. Nothing is inferred from the advertisement, the profile or the software
/// identity, and an empty <see cref="SearchCertification"/> is a deliberate statement that nothing extra is proven.
/// Immutable.</para>
/// </remarks>
public sealed class ShardCapabilityProof
{
    /// <summary>Initializes a new instance of the <see cref="ShardCapabilityProof"/> class.</summary>
    /// <param name="shard">The registered shard the proof is for.</param>
    /// <param name="evidence">The advertisement instance the proof was established against.</param>
    /// <param name="certification">What the caller has proven that shard answers exactly.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ShardCapabilityProof(ShardDescriptor shard, ShardCapabilityEvidence evidence, SearchCertification certification)
    {
        ArgumentNullException.ThrowIfNull(shard);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(certification);

        Shard = shard;
        Evidence = evidence;
        Certification = certification;
    }

    /// <summary>Gets the shard descriptor the proof is bound to.</summary>
    public ShardDescriptor Shard { get; }

    /// <summary>Gets the advertisement instance the proof is bound to.</summary>
    public ShardCapabilityEvidence Evidence { get; }

    /// <summary>Gets the supplied proof.</summary>
    public SearchCertification Certification { get; }
}
