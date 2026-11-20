using System.Collections.Frozen;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Search;

/// <summary>The reference hop behind a chain, include or reverse include, with everything the executor needs and nothing it must re-derive.</summary>
public sealed class ReferenceHop
{
    internal ReferenceHop(
        ReferencePath path,
        Uri definition,
        string expression,
        ChainResolutionMode? mode,
        ParameterOccurrence? terminal,
        FrozenDictionary<ShardId, ReferencePathCertification> certifications)
    {
        Path = path;
        Definition = definition;
        Expression = expression;
        Mode = mode;
        Terminal = terminal;
        Certifications = certifications;
    }

    /// <summary>Gets the typed path.</summary>
    public ReferencePath Path { get; }

    /// <summary>Gets the R4 definition canonical of the reference parameter.</summary>
    public Uri Definition { get; }

    /// <summary>Gets the R4 FHIRPath expression that extracts the reference from a <see cref="ReferencePath.Source"/> resource. Not evaluated here.</summary>
    public string Expression { get; }

    /// <summary>Gets where a chain is joined, or <see langword="null"/> for an include or reverse include.</summary>
    public ChainResolutionMode? Mode { get; }

    /// <summary>
    /// Gets the terminal of a Distributed chain as one URI-encoded occurrence, its decoded modifier and value kept exactly:
    /// the sub-query to run on <see cref="ReferencePath.Target"/> for a forward chain, or on <see cref="ReferencePath.Source"/>
    /// for a reverse chain. <see langword="null"/> otherwise.
    /// </summary>
    public ParameterOccurrence? Terminal { get; }

    /// <summary>Gets each shard's certification for <see cref="Path"/>, including the reference spellings it matches.</summary>
    public IReadOnlyDictionary<ShardId, ReferencePathCertification> Certifications { get; }
}
