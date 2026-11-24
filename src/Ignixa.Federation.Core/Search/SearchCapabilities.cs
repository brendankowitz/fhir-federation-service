using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Ignixa.Search.Models;
using Ignixa.Specification.ValueSets.Normative;

namespace Ignixa.Federation.Core.Search;

/// <summary>
/// What every registered shard can search, admitted once from one explicit capability per shard: the published
/// intersection for <c>/metadata</c> and the proofs the planner checks (design spec §9).
/// </summary>
/// <remarks>
/// A parameter is published only under the selected release's meaning: its definition with that exact code and type, and every
/// occurrence every shard declares names either no definition or exactly the selected canonical (ordinal; version and fragment
/// count; no URI normalization). A shard that binds a code to another meaning loses the code; meanings are never
/// rebound. Immutable; safe to share across threads.
/// </remarks>
public sealed class SearchCapabilities
{
    private readonly FrozenDictionary<ResourceType, Published> _resources;
    private readonly ShardSearchCapability[] _shards;

    /// <summary>Initializes a new instance of the <see cref="SearchCapabilities"/> class.</summary>
    /// <param name="registry">The shard registry.</param>
    /// <param name="shards">Exactly one capability per registered shard.</param>
    /// <param name="specification">The release-specific search semantics for this view.</param>
    /// <exception cref="ArgumentException">A registered shard has no capability, or a capability is unregistered or repeated.</exception>
    public SearchCapabilities(ShardRegistry registry, IEnumerable<ShardSearchCapability> shards, IFederationSearchSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(specification);
        ShardSearchCapability[] supplied = [.. shards];
        if (supplied.DistinctBy(s => s.Shard).Count() != supplied.Length)
        {
            throw new ArgumentException("Each shard has one search capability.", nameof(shards));
        }

        if (supplied.Any(s => !registry.TryGetById(s.Shard, out _)))
        {
            throw new ArgumentException("A search capability names a shard that is not registered.", nameof(shards));
        }

        if (supplied.Any(s => !specification.AcceptedShardVersions.Contains(s.Version)))
        {
            throw new ArgumentException("A shard advertises a version outside this federation view.", nameof(shards));
        }

        _shards = [.. registry.Shards.Select(d => supplied.SingleOrDefault(s => s.Shard == d.Id)
            ?? throw new ArgumentException($"Shard '{d.Id.Value}' has no search capability.", nameof(shards)))];

        Registry = registry;
        Specification = specification;
        _resources = _shards[0].Resources
            .Select(r => r.Type)
            .Where(t => Specification.IsResourceType(t.Value) && _shards.All(s => s.Resources.Any(r => r.Type == t)))
            .ToFrozenDictionary(t => t, t => Intersect(t, [.. _shards.Select(s => s.Resources.Single(r => r.Type == t))]));
        Resources = new ReadOnlyCollection<ResourceSearchSupport>(
            [.. _resources.OrderBy(p => p.Key.Value, StringComparer.Ordinal).Select(p => Project(p.Key, p.Value))]);
        SupportsLastUpdatedSort = registry.Shards.All(d => d.Profile.LastUpdatedOrder?.StableTies == true);
    }

    /// <summary>Gets the shard registry the capabilities were admitted against.</summary>
    public ShardRegistry Registry { get; }

    /// <summary>Gets the release-specific semantics used to admit this snapshot.</summary>
    public IFederationSearchSpecification Specification { get; }

    /// <summary>
    /// Gets the resource types every shard searches, ordered by name. A listed parameter has a common selected-release definition
    /// and a representative value that plans as one unmodified predicate on that type. This does not promise every
    /// value, modifier or combination. Composite and special parameters have no general witness and are not listed.
    /// </summary>
    /// <remarks>
    /// Reference parameters are not listed. A caller's identity filter is refused
    /// (<see cref="SearchFailureCode.UnsupportedReferenceFilter"/>), independently of support for <c>:missing</c>,
    /// chains or includes. An include or revinclude is listed only when every shard advertises that exact form and
    /// it plans alone. A certified typed form covered by an advertised untyped form can plan without being listed.
    /// </remarks>
    public IReadOnlyList<ResourceSearchSupport> Resources { get; }

    /// <summary>Gets a value indicating whether every shard's profile certifies a <c>_lastUpdated</c> order with stable ties.</summary>
    public bool SupportsLastUpdatedSort { get; }

    internal bool Searches(ResourceType type) => _resources.ContainsKey(type);

    /// <summary>Finds the selected-release definition of <paramref name="code"/> on <paramref name="type"/> when every shard advertises it.</summary>
    internal SearchParameterInfo? FindParameter(ResourceType type, string code) =>
        _resources.TryGetValue(type, out Published? published) ? published.Parameters.GetValueOrDefault(code) : null;

    internal bool AdvertisesInclude(ResourceType type, string include) =>
        _resources.TryGetValue(type, out Published? published) && published.Includes.Contains(include);

    internal bool AdvertisesRevInclude(ResourceType type, string revInclude) =>
        _resources.TryGetValue(type, out Published? published) && published.RevIncludes.Contains(revInclude);

    internal bool EveryShard(Func<SearchCertification, bool> proven) => _shards.All(s => proven(s.Certification));

    /// <summary>Gets a value indicating whether every shard proves comma alternatives for <paramref name="type"/>.</summary>
    internal bool ProvesAlternatives(SearchParamType type) => EveryShard(c => c.OrAlternatives.Contains(type));

    /// <summary>
    /// Admits <c>_include=Source:code[:Target]</c>, or <c>_revinclude</c> when <paramref name="reverse"/>, searched from
    /// <paramref name="searched"/>; the federation answers it by extracting references from the page, or by a follow-up
    /// search of the source type. The one admission rule for requests and for <see cref="Resources"/>.
    /// </summary>
    /// <param name="searched">The searched resource type.</param>
    /// <param name="value">The include value.</param>
    /// <param name="reverse">Whether the value is a revinclude.</param>
    /// <param name="hop">The certified hop when admitted.</param>
    /// <param name="failure">
    /// When refused: <see cref="SearchFailureCode.InvalidValue"/> for a value that names no reference from or to
    /// <paramref name="searched"/>, otherwise <see cref="SearchFailureCode.UnsupportedInclude"/>.
    /// </param>
    /// <returns>Whether the value is admitted.</returns>
    internal bool TryAdmitInclude(ResourceType searched, string value, bool reverse, [NotNullWhen(true)] out ReferenceHop? hop, out SearchFailureCode failure)
    {
        hop = null;
        failure = SearchFailureCode.UnsupportedInclude;
        string[] parts = value.Split(':');
        if (parts is ["*"] or [_, "*", ..])
        {
            return false;
        }

        ResourceType? typed = null;
        if (parts.Length is not (2 or 3)
            || !ResourceType.TryParse(parts[0], out ResourceType? source)
            || !SearchSyntax.IsSearchCode(parts[1])
            || Specification.FindDefinition(source, parts[1]) is not { Type: SearchParamType.Reference } definition
            || (parts.Length == 3 && !ResourceType.TryParse(parts[2], out typed)))
        {
            failure = SearchFailureCode.InvalidValue;
            return false;
        }

        string code = parts[1];
        string[] targets = [.. definition.TargetResourceTypes];
        ResourceType target;
        if (reverse)
        {
            if (!targets.Contains(searched.Value, StringComparer.Ordinal) || (typed is not null && typed != searched))
            {
                failure = SearchFailureCode.InvalidValue;
                return false;
            }

            target = searched;
        }
        else if (source != searched || (typed is not null && !targets.Contains(typed.Value, StringComparer.Ordinal)))
        {
            failure = SearchFailureCode.InvalidValue;
            return false;
        }
        else if (typed is not null)
        {
            target = typed;
        }
        else if (targets is [string only])
        {
            target = ResourceType.Parse(only);
        }
        else
        {
            // Including every target type of a polymorphic reference is not certified per target.
            return false;
        }

        // An advertised untyped include covers each typed form of it.
        Func<ResourceType, string, bool> advertises = reverse ? AdvertisesRevInclude : AdvertisesInclude;
        var path = new ReferencePath(source, code, target);
        if (!(advertises(searched, value) || advertises(searched, $"{source.Value}:{code}"))
            || !Specification.IsPlainReference(definition, source)
            || CertifiedPath(path, reverse ? ReferencePathOperations.RevIncludeSearch : ReferencePathOperations.IncludeExtraction) is not { } certifications
            || (reverse && certifications.Values.Any(c => c.RevIncludeSearchShape is null))
            || !ProvesAlternatives(reverse ? SearchParamType.Reference : SearchParamType.Token)
            || (reverse ? FindParameter(source, code) : FindParameter(target, "_id")) is null)
        {
            return false;
        }

        hop = new ReferenceHop(path, definition.Url, definition.Expression, null, null, certifications);
        return true;
    }

    /// <summary>
    /// Gets every shard's certification of <paramref name="path"/> when each proves all of <paramref name="required"/>. A
    /// reference-search operation also needs coverage attested for the current <see cref="Registry"/> version: coverage of
    /// another registry says nothing about references stored under its bases, so it proves no reference search. A stale
    /// attestation leaves the shard's other operations proven.
    /// </summary>
    internal FrozenDictionary<ShardId, ReferencePathCertification>? CertifiedPath(ReferencePath path, ReferencePathOperations required)
    {
        bool searchesByReference = (required & (ReferencePathOperations.DistributedForward | ReferencePathOperations.RevIncludeSearch)) != 0;
        var certified = new Dictionary<ShardId, ReferencePathCertification>(_shards.Length);
        foreach (ShardSearchCapability shard in _shards)
        {
            if (shard.Certification.FindPath(path) is not { } certification
                || (certification.Operations & required) != required
                || (searchesByReference && !string.Equals(certification.CoveredRegistryVersion, Registry.Version, StringComparison.Ordinal)))
            {
                return null;
            }

            certified.Add(shard.Shard, certification);
        }

        return certified.ToFrozenDictionary();
    }

    private Published Intersect(ResourceType type, ResourceSearchSupport[] perShard)
    {
        IEnumerable<string> codes = perShard[0].Parameters.Select(p => p.Code).Distinct(StringComparer.Ordinal);
        var parameters = new SortedDictionary<string, SearchParameterInfo>(StringComparer.Ordinal);
        foreach (string code in codes)
        {
            if (Specification.FindDefinition(type, code) is { } definition
                && perShard.All(r => r.Parameters.Any(p => p.Code == code)
                    && r.Parameters.Where(p => p.Code == code).All(p =>
                        p.Type == definition.Type
                        && (p.Definition is null
                            || string.Equals(p.Definition, definition.Url.OriginalString, StringComparison.Ordinal)))))
            {
                parameters.Add(code, definition);
            }
        }

        return new Published(
            parameters.ToFrozenDictionary(StringComparer.Ordinal),
            IntersectIncludes(perShard.Select(r => r.SearchIncludes)),
            IntersectIncludes(perShard.Select(r => r.SearchRevIncludes)));
    }

    // Every form all shards advertise stays internal, so a withheld untyped include still covers its typed forms.
    private static FrozenSet<string> IntersectIncludes(IEnumerable<IReadOnlyList<string>> perShard) =>
        perShard.Select(forms => forms.AsEnumerable())
            .Aggregate((left, right) => left.Intersect(right, StringComparer.Ordinal))
            .ToFrozenSet(StringComparer.Ordinal);

    private ResourceSearchSupport Project(ResourceType type, Published published) => new(
        type,
        published.Parameters.Values
            .Where(d => SearchPlanner.CanPublishParameter(Specification, type, d))
            .OrderBy(d => d.Code, StringComparer.Ordinal)
            .Select(d => new SearchParameterSupport(d.Code, d.Type, d.Url.OriginalString)),
        Admitted(type, published.Includes, reverse: false),
        Admitted(type, published.RevIncludes, reverse: true));

    private string[] Admitted(ResourceType type, FrozenSet<string> advertised, bool reverse) =>
        [.. advertised.Where(form => TryAdmitInclude(type, form, reverse, out _, out _)).Order(StringComparer.Ordinal)];

    private sealed record Published(
        FrozenDictionary<string, SearchParameterInfo> Parameters,
        FrozenSet<string> Includes,
        FrozenSet<string> RevIncludes);
}
