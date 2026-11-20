using System.Collections.Frozen;
using Ignixa.Specification.ValueSets.Normative;

namespace Ignixa.Federation.Core.Search;

/// <summary>
/// What one shard is proven to answer exactly, beyond what its CapabilityStatement advertises: modifiers, repetition,
/// alternatives, exact counts and reference paths (design spec §§6.2, 9.2). Nothing here is inferred from a profile name.
/// </summary>
/// <remarks>Immutable; safe to share across threads.</remarks>
public sealed class SearchCertification
{
    /// <summary>The modifier entry that certifies a resource-type modifier, such as <c>subject:Patient</c>, on reference parameters.</summary>
    public const string TypeModifier = "[type]";

    private readonly FrozenDictionary<SearchParamType, FrozenSet<string>> _modifiers;
    private readonly FrozenDictionary<ReferencePath, ReferencePathCertification> _paths;

    /// <summary>Initializes a new instance of the <see cref="SearchCertification"/> class.</summary>
    /// <param name="modifiers">The modifiers proven per parameter type, such as <c>exact</c> for string; <see cref="TypeModifier"/> for typed references.</param>
    /// <param name="andRepetition">The parameter types whose repetition (<c>a=1&amp;a=2</c>) the shard ANDs exactly.</param>
    /// <param name="orAlternatives">The parameter types whose alternatives (<c>a=1,2</c>) the shard ORs exactly.</param>
    /// <param name="exactCountShapes">The normalized query shapes, such as <c>Patient?gender=,&amp;name:exact</c>, the shard counts exactly.</param>
    /// <param name="referencePaths">The reference paths proven, at most one certification each.</param>
    /// <exception cref="ArgumentException">A modifier or shape is blank or malformed, or a path is certified twice.</exception>
    public SearchCertification(
        IReadOnlyDictionary<SearchParamType, IReadOnlyCollection<string>> modifiers,
        IEnumerable<SearchParamType> andRepetition,
        IEnumerable<SearchParamType> orAlternatives,
        IEnumerable<string> exactCountShapes,
        IEnumerable<ReferencePathCertification> referencePaths)
    {
        ArgumentNullException.ThrowIfNull(modifiers);
        foreach (string modifier in modifiers.Values.SelectMany(m => m))
        {
            if (modifier != TypeModifier && !R4Search.IsSearchCode(modifier))
            {
                throw new ArgumentException("A modifier is a search code such as 'exact', or '[type]'.", nameof(modifiers));
            }
        }

        _modifiers = modifiers.ToFrozenDictionary(p => p.Key, p => p.Value.ToFrozenSet(StringComparer.Ordinal));
        AndRepetition = andRepetition.ToFrozenSet();
        OrAlternatives = orAlternatives.ToFrozenSet();
        ExactCountShapes = exactCountShapes.ToFrozenSet(StringComparer.Ordinal);
        if (ExactCountShapes.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("An exact-count shape is not blank.", nameof(exactCountShapes));
        }

        var paths = new Dictionary<ReferencePath, ReferencePathCertification>();
        ArgumentNullException.ThrowIfNull(referencePaths);
        foreach (ReferencePathCertification path in referencePaths)
        {
            if (!paths.TryAdd(path.Path, path))
            {
                throw new ArgumentException($"The reference path {path.Path} is certified twice.", nameof(referencePaths));
            }
        }

        _paths = paths.ToFrozenDictionary();
    }

    /// <summary>Gets the parameter types whose repetition is proven.</summary>
    public IReadOnlySet<SearchParamType> AndRepetition { get; }

    /// <summary>Gets the parameter types whose alternatives are proven.</summary>
    public IReadOnlySet<SearchParamType> OrAlternatives { get; }

    /// <summary>Gets the normalized query shapes counted exactly, compared ordinally.</summary>
    public IReadOnlySet<string> ExactCountShapes { get; }

    /// <summary>Gets the reference path certifications.</summary>
    public IEnumerable<ReferencePathCertification> ReferencePaths => _paths.Values;

    /// <summary>Gets a value indicating whether <paramref name="modifier"/> is proven for parameters of <paramref name="type"/>.</summary>
    /// <param name="type">The parameter type.</param>
    /// <param name="modifier">The modifier text, or <see cref="TypeModifier"/>.</param>
    /// <returns><see langword="true"/> when proven.</returns>
    public bool SupportsModifier(SearchParamType type, string modifier) =>
        _modifiers.TryGetValue(type, out FrozenSet<string>? proven) && proven.Contains(modifier);

    /// <summary>Gets the certification for <paramref name="path"/>, if any.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The certification, or <see langword="null"/>.</returns>
    public ReferencePathCertification? FindPath(ReferencePath path) => _paths.GetValueOrDefault(path);
}
