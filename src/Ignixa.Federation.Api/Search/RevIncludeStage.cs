using Ignixa.Federation.Api.Authorization;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Api.Search;

/// <summary>
/// The reverse-include work of one request, computed from the current plan and the caller's current grants. Never
/// carried in a continuation: each page decides it again, so a downgrade takes effect on the next page.
/// </summary>
/// <param name="Plan">The exact current plan, including requests for source types the caller may not read.</param>
/// <param name="Readable">The primary type and the authorized source types, which is all the resolver may consult.</param>
/// <param name="Sources">The requested source types the caller may read.</param>
/// <param name="Denied">Whether any requested source type is unreadable to the caller.</param>
internal sealed record RevIncludeStage(QueryPlan Plan, IReadOnlySet<ResourceType> Readable, IReadOnlySet<ResourceType> Sources, bool Denied)
{
    /// <summary>Builds the stage for a plan.</summary>
    /// <param name="plan">The current plan.</param>
    /// <param name="access">The caller's current grants.</param>
    /// <returns>The stage, or <see langword="null"/> when the plan requests no reverse include.</returns>
    public static RevIncludeStage? Create(QueryPlan plan, CallerAccess access)
    {
        ResourceType[] requested =
        [
            .. plan.Occurrences.Where(o => o.Kind == OccurrenceKind.RevInclude).Select(o => o.Hop!.Path.Source).Distinct(),
        ];
        if (requested.Length == 0)
        {
            return null;
        }

        HashSet<ResourceType> sources = [.. requested.Where(access.CanRead)];
        return new RevIncludeStage(plan, new HashSet<ResourceType>(sources) { plan.Type }, sources, sources.Count != requested.Length);
    }

    /// <summary>Whether a resolver's result is something this stage may serialize: within the limit and only authorized, planned source types.</summary>
    /// <param name="result">The result.</param>
    /// <param name="limit">The include limit handed to the resolver.</param>
    /// <returns><see langword="true"/> when every include may be served.</returns>
    public bool Accepts(RevIncludePageResult result, int limit) =>
        result.Includes.Count <= limit && result.Includes.All(e => Sources.Contains(e.Key.Type));
}
