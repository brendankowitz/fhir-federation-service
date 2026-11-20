using System.Collections.Frozen;
using System.Collections.ObjectModel;
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
/// A parameter is published only under its R4 meaning: the R4 definition with that exact code, the same type, and, when
/// the shard names a definition, the R4 canonical. A shard that binds a code to another meaning loses the code; meanings
/// are never rebound. Immutable; safe to share across threads.
/// </remarks>
public sealed class SearchCapabilities
{
    private readonly FrozenDictionary<ResourceType, Published> _resources;
    private readonly ShardSearchCapability[] _shards;

    /// <summary>Initializes a new instance of the <see cref="SearchCapabilities"/> class.</summary>
    /// <param name="registry">The shard registry.</param>
    /// <param name="shards">Exactly one capability per registered shard.</param>
    /// <exception cref="ArgumentException">A registered shard has no capability, or a capability is unregistered or repeated.</exception>
    public SearchCapabilities(ShardRegistry registry, IEnumerable<ShardSearchCapability> shards)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ShardSearchCapability[] supplied = [.. shards];
        if (supplied.DistinctBy(s => s.Shard).Count() != supplied.Length)
        {
            throw new ArgumentException("Each shard has one search capability.", nameof(shards));
        }

        if (supplied.Any(s => !registry.TryGetById(s.Shard, out _)))
        {
            throw new ArgumentException("A search capability names a shard that is not registered.", nameof(shards));
        }

        _shards = [.. registry.Shards.Select(d => supplied.SingleOrDefault(s => s.Shard == d.Id)
            ?? throw new ArgumentException($"Shard '{d.Id.Value}' has no search capability.", nameof(shards)))];

        Registry = registry;
        _resources = _shards[0].Resources
            .Select(r => r.Type)
            .Where(t => R4Search.IsResourceType(t.Value) && _shards.All(s => s.Resources.Any(r => r.Type == t)))
            .ToFrozenDictionary(t => t, t => Intersect(t, [.. _shards.Select(s => s.Resources.Single(r => r.Type == t))]));
        Resources = new ReadOnlyCollection<ResourceSearchSupport>(
            [.. _resources.Values.Select(p => p.Support).OrderBy(r => r.Type.Value, StringComparer.Ordinal)]);
        SupportsLastUpdatedSort = registry.Shards.All(d => d.Profile.LastUpdatedOrder?.StableTies == true);
    }

    /// <summary>Gets the shard registry the capabilities were admitted against.</summary>
    public ShardRegistry Registry { get; }

    /// <summary>
    /// Gets the resource types every shard searches, ordered by name, each with the parameters and includes every shard
    /// supports. Reference parameters are not listed: a caller's reference value is refused
    /// (<see cref="SearchFailureCode.UnsupportedReferenceFilter"/>), and an entry cannot say that only <c>:missing</c>,
    /// chains and includes are planned. They still serve the chains, includes and revincludes every shard certifies.
    /// </summary>
    public IReadOnlyList<ResourceSearchSupport> Resources { get; }

    /// <summary>Gets a value indicating whether every shard's profile certifies a <c>_lastUpdated</c> order with stable ties.</summary>
    public bool SupportsLastUpdatedSort { get; }

    internal bool Searches(ResourceType type) => _resources.ContainsKey(type);

    /// <summary>Finds the R4 definition of <paramref name="code"/> on <paramref name="type"/> when every shard advertises it.</summary>
    internal SearchParameterInfo? FindParameter(ResourceType type, string code) =>
        _resources.TryGetValue(type, out Published? published) ? published.Parameters.GetValueOrDefault(code) : null;

    internal bool AdvertisesInclude(ResourceType type, string include) =>
        _resources.TryGetValue(type, out Published? published) && published.Includes.Contains(include);

    internal bool AdvertisesRevInclude(ResourceType type, string revInclude) =>
        _resources.TryGetValue(type, out Published? published) && published.RevIncludes.Contains(revInclude);

    internal bool EveryShard(Func<SearchCertification, bool> proven) => _shards.All(s => proven(s.Certification));

    /// <summary>Gets every shard's certification of <paramref name="path"/> when each proves all of <paramref name="required"/>.</summary>
    internal FrozenDictionary<ShardId, ReferencePathCertification>? CertifiedPath(ReferencePath path, ReferencePathOperations required)
    {
        var certified = new Dictionary<ShardId, ReferencePathCertification>(_shards.Length);
        foreach (ShardSearchCapability shard in _shards)
        {
            if (shard.Certification.FindPath(path) is not { } certification || (certification.Operations & required) != required)
            {
                return null;
            }

            certified.Add(shard.Shard, certification);
        }

        return certified.ToFrozenDictionary();
    }

    private static Published Intersect(ResourceType type, ResourceSearchSupport[] perShard)
    {
        IEnumerable<string> codes = perShard[0].Parameters.Select(p => p.Code).Distinct(StringComparer.Ordinal);
        var parameters = new SortedDictionary<string, SearchParameterInfo>(StringComparer.Ordinal);
        foreach (string code in codes)
        {
            if (R4Search.FindDefinition(type, code) is { } definition
                && perShard.All(r => r.Parameters.Any(p => p.Code == code)
                    && r.Parameters.Where(p => p.Code == code).All(p => p.Type == definition.Type && (p.Definition is null || p.Definition == definition.Url))))
            {
                parameters.Add(code, definition);
            }
        }

        string[] includes = IntersectIncludes(perShard.Select(r => r.SearchIncludes));
        string[] revIncludes = IntersectIncludes(perShard.Select(r => r.SearchRevIncludes));
        var support = new ResourceSearchSupport(
            type,
            parameters.Values.Where(d => d.Type != SearchParamType.Reference).Select(d => new SearchParameterSupport(d.Code, d.Type, d.Url)),
            includes,
            revIncludes);
        return new Published(
            support,
            parameters.ToFrozenDictionary(StringComparer.Ordinal),
            includes.ToFrozenSet(StringComparer.Ordinal),
            revIncludes.ToFrozenSet(StringComparer.Ordinal));
    }

    private static string[] IntersectIncludes(IEnumerable<IReadOnlyList<string>> perShard) =>
    [
        .. perShard.Select(forms => forms.AsEnumerable())
            .Aggregate((left, right) => left.Intersect(right, StringComparer.Ordinal))
            .Where(form => form.Split(':') switch
            {
                [string source, string code] => R4Search.IsResourceType(source) && R4Search.IsSearchCode(code),
                [string source, string code, string target] =>
                    R4Search.IsResourceType(source) && R4Search.IsSearchCode(code) && R4Search.IsResourceType(target),
                _ => false,
            })
            .Order(StringComparer.Ordinal),
    ];

    private sealed record Published(
        ResourceSearchSupport Support,
        FrozenDictionary<string, SearchParameterInfo> Parameters,
        FrozenSet<string> Includes,
        FrozenSet<string> RevIncludes);
}
