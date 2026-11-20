using System.Diagnostics;
using Ignixa.FhirPath.Expressions;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.Includes;

/// <summary>
/// Resolves one page's forward includes (design spec §7.6): reads each planned include's references from the page's
/// matches, routes them to their owning shards, and fetches each shard's targets of one type with one <c>_id</c> search.
/// </summary>
/// <remarks>
/// <para>
/// Only the emitted matches are read, so a reference in lookahead the page did not emit is never fetched. Targets are
/// identified by shard, type and id; the same relative id from two shards is two targets. Target groups are fetched one at
/// a time, in the order their first reference was found (match order, then include order, then expression order), and
/// each keeps its backend's page and entry order.
/// </para>
/// <para>
/// A target whose shard, type and id is a match on the page is never requested, so a match is never repeated as an
/// include, even when a read would return a newer version; a page whose only targets are matches makes no call. A backend
/// row for a match key is therefore an unrequested row and breaks the shard contract. Other included resources are
/// emitted once per resource key and version. The page's includes stop at the limit: only a further distinct, validated
/// resource proves the limit was exceeded and adds <see cref="IncludeOutcomeKind.Truncated"/>. A versioned reference,
/// including one to a match, adds <see cref="IncludeOutcomeKind.VersionedReference"/>; a fetched target is read at its
/// current version. Shard faults, contract violations and budget exhaustion fail the call; they are never outcomes.
/// </para>
/// <para>
/// Each group's <c>_id</c> list is sent in one request whatever its length. <see cref="BackendProfile.MaxCount"/> bounds
/// only the page size asked for, not the size of the request. Request-size enforcement (a GET request line or POST body
/// the backend accepts) is a prerequisite for public use and is not provided here.
/// </para>
/// <para>The resolver is stateless between calls and safe to share.</para>
/// </remarks>
public sealed class ForwardIncludeResolver
{
    private readonly IShardClient _client;
    private readonly ShardRegistry _registry;
    private readonly ReferenceClassifier _classifier;
    private readonly int _maxIncludesPerPage;

    /// <summary>Initializes a new instance of the <see cref="ForwardIncludeResolver"/> class.</summary>
    /// <param name="client">The client that calls shards.</param>
    /// <param name="registry">The registry that routes references and holds each shard's backend profile.</param>
    /// <param name="maxIncludesPerPage">The most included resources one page holds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="registry"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxIncludesPerPage"/> is less than 1.</exception>
    public ForwardIncludeResolver(IShardClient client, ShardRegistry registry, int maxIncludesPerPage)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIncludesPerPage, 1);
        _client = client;
        _registry = registry;
        _classifier = new ReferenceClassifier(registry);
        _maxIncludesPerPage = maxIncludesPerPage;
    }

    /// <summary>Resolves the forward includes of one page.</summary>
    /// <param name="matches">The page's emitted matches, in emission order.</param>
    /// <param name="forwardIncludes">The plan's forward includes, in query order.</param>
    /// <param name="authorizedTypes">
    /// The types the caller may read. An include whose target type is not here is reported as
    /// <see cref="IncludeOutcomeKind.UnauthorizedType"/> and never evaluated.
    /// </param>
    /// <param name="fetchBudget">
    /// The most shard calls the includes may start, each initial search and each <c>next</c> follow. Zero is valid when no
    /// call is needed.
    /// </param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>The included resources, the outcomes to report and the calls started.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="matches"/>, <paramref name="forwardIncludes"/> or <paramref name="authorizedTypes"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="matches"/> holds <see langword="null"/>, or <paramref name="forwardIncludes"/> holds an occurrence that
    /// is not a planned forward include. Thrown before any shard call.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fetchBudget"/> is negative.</exception>
    /// <exception cref="ShardFailureException">
    /// A match is not a valid resource of its key's type (its body cannot be read, wherever in the body the fault lies, or a
    /// reference it holds is not a single JSON string), or a target shard failed, timed out or broke the shard contract: a
    /// row it was not asked for, a duplicate on one page, or a <c>next</c> chain that repeats or cycles.
    /// </exception>
    /// <exception cref="FetchBudgetExhaustedException">The includes need a shard call beyond <paramref name="fetchBudget"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<IncludeResult> ResolveAsync(
        IReadOnlyList<ShardEntry> matches,
        IReadOnlyList<PlannedOccurrence> forwardIncludes,
        IReadOnlySet<ResourceType> authorizedTypes,
        int fetchBudget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(authorizedTypes);
        ArgumentOutOfRangeException.ThrowIfNegative(fetchBudget);
        ReferenceHop[] hops = HopsOf(forwardIncludes);
        if (matches.Any(match => match is null))
        {
            throw new ArgumentException("Matches must not contain null.", nameof(matches));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var outcomes = new List<IncludeOutcome>();
        var authorized = new List<ReferenceHop>(hops.Length);
        foreach (ReferenceHop hop in hops)
        {
            if (authorizedTypes.Contains(hop.Path.Target))
            {
                authorized.Add(hop);
            }
            else
            {
                Report(outcomes, IncludeOutcomeKind.UnauthorizedType, hop.Path);
            }
        }

        List<TargetGroup> groups = Discover(matches, authorized, outcomes, cancellationToken);

        var includes = new List<ShardEntry>();
        bool truncated = false;
        var scheduler = new FetchScheduler(_client, fetchBudget, 1, cancellationToken);
        await using (scheduler)
        {
            // Matches are never requested, so this only dedupes includes: one key may recur across a group's backend pages.
            var emitted = new HashSet<(ResourceKey, string?)>();
            foreach (TargetGroup group in groups)
            {
                if (await FetchAsync(group, includes, emitted, scheduler, cancellationToken))
                {
                    truncated = true;
                    break;
                }
            }
        }

        if (truncated)
        {
            Report(outcomes, IncludeOutcomeKind.Truncated, null);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Read after the join, like the page executor, so the count is final.
        return new IncludeResult(includes.AsReadOnly(), outcomes.AsReadOnly(), scheduler.Started);
    }

    private static ReferenceHop[] HopsOf(IReadOnlyList<PlannedOccurrence> forwardIncludes)
    {
        ArgumentNullException.ThrowIfNull(forwardIncludes);
        var hops = new ReferenceHop[forwardIncludes.Count];
        for (int i = 0; i < hops.Length; i++)
        {
            hops[i] = forwardIncludes[i] is { Kind: OccurrenceKind.Include, Hop: { } hop }
                ? hop
                : throw new ArgumentException("Every occurrence must be a planned forward include.", nameof(forwardIncludes));
        }

        return hops;
    }

    private static void Report(List<IncludeOutcome> outcomes, IncludeOutcomeKind kind, ReferencePath? path)
    {
        if (!outcomes.Exists(outcome => outcome.Kind == kind && outcome.Path == path))
        {
            outcomes.Add(new IncludeOutcome(kind, path));
        }
    }

    /// <summary>
    /// Reads every authorized include's references from the matches of its source type and groups the routable ones of
    /// its target type by owning shard and type, in first-discovery order, each target once. A target that is itself a
    /// match is left out, so a group exists only when it has a target to request. Each include's expression is parsed
    /// once, not once per match.
    /// </summary>
    private List<TargetGroup> Discover(
        IReadOnlyList<ShardEntry> matches,
        List<ReferenceHop> hops,
        List<IncludeOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        var parsed = new (ReferenceHop Hop, Expression Expression)[hops.Count];
        for (int i = 0; i < parsed.Length; i++)
        {
            parsed[i] = (hops[i], IncludeReferenceExtractor.ParseExpression(hops[i]));
        }

        var matchKeys = new HashSet<ResourceKey>(matches.Select(match => match.Key));
        var groups = new List<TargetGroup>();
        var byTarget = new Dictionary<(ShardId, ResourceType), TargetGroup>();
        foreach (ShardEntry match in matches)
        {
            foreach ((ReferenceHop hop, Expression expression) in parsed)
            {
                if (match.Key.Type != hop.Path.Source)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                foreach (string literal in IncludeReferenceExtractor.Extract(match, expression))
                {
                    ReferenceClassification classification = _classifier.Classify(match.Key.Shard, literal);
                    if (classification is not { Kind: ReferenceKind.Relative or ReferenceKind.KnownAbsolute, Target: { } target }
                        || target.Type != hop.Path.Target)
                    {
                        continue;
                    }

                    if (classification.Version is not null)
                    {
                        Report(outcomes, IncludeOutcomeKind.VersionedReference, hop.Path);
                    }

                    // A match is the current resource of its key on this page (spec §4.1, §7.6), whatever a read returns now.
                    if (matchKeys.Contains(target))
                    {
                        continue;
                    }

                    if (!byTarget.TryGetValue((target.Shard, target.Type), out TargetGroup? group))
                    {
                        group = new TargetGroup(target.Shard, target.Type);
                        byTarget.Add((target.Shard, target.Type), group);
                        groups.Add(group);
                    }

                    group.Add(target.Id);
                }
            }
        }

        return groups;
    }

    /// <summary>
    /// Fetches one group's targets into <paramref name="includes"/>, following <c>next</c> until the backend is exhausted or
    /// a distinct resource beyond the limit proves overflow.
    /// </summary>
    /// <returns><see langword="true"/> when the page's includes overflowed.</returns>
    private async Task<bool> FetchAsync(
        TargetGroup group,
        List<ShardEntry> includes,
        HashSet<(ResourceKey, string?)> emitted,
        FetchScheduler scheduler,
        CancellationToken cancellationToken)
    {
        ShardDescriptor descriptor = _registry.TryGetById(group.Shard, out ShardDescriptor? found)
            ? found
            : throw new UnreachableException("The classifier routes only to registered shards.");
        ShardSearchMethod method = descriptor.Profile.SupportsPostSearch ? ShardSearchMethod.PostSearch : ShardSearchMethod.Get;
        var template = new ShardSearchRequest(
            group.Shard,
            group.Type,
            [new ParameterOccurrence("_id", group.IdValue)],
            group.Count,
            method);
        var stream = new ShardStream(template, descriptor);

        // One more than the free slots, so a full answer can prove overflow; long, because the limit may be int.MaxValue.
        int wanted = (int)Math.Min(group.Count, (long)_maxIncludesPerPage - includes.Count + 1);
        ShardSearchRequest first = stream.FirstPage(wanted);
        var redeemed = new HashSet<ShardCursor>();
        CursorCycleState cycle = CursorCycleState.Initial;
        ShardCursor? cursor = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShardPage page = cursor is null ? await scheduler.SearchAsync(first) : await scheduler.ContinueAsync(cursor);

            // Validate the whole page before acting on any of it, so a bad row after the overflow witness still fails. A row
            // for a match was never requested, so it fails here too.
            stream.Validate(page, redeemed);
            if (!page.Entries.All(entry => group.Requested(entry.Key.Id)))
            {
                throw new ShardFailureException(group.Shard, ShardFailureReason.ContractViolation, null);
            }

            foreach (ShardEntry entry in page.Entries)
            {
                if (!emitted.Add((entry.Key, entry.VersionId)))
                {
                    continue;
                }

                if (includes.Count == _maxIncludesPerPage)
                {
                    return true;
                }

                includes.Add(entry);
            }

            if (page.Next is null)
            {
                return false;
            }

            cycle = stream.Follow(cycle, page.Next);
            redeemed.Add(page.Next);
            cursor = page.Next;
        }
    }

    /// <summary>The distinct targets of one shard and type, in first-discovery order.</summary>
    private sealed class TargetGroup(ShardId shard, ResourceType type)
    {
        private readonly List<ResourceId> _ids = [];
        private readonly HashSet<ResourceId> _requested = [];

        public ShardId Shard { get; } = shard;

        public ResourceType Type { get; } = type;

        public int Count => _ids.Count;

        /// <summary>Gets the <c>_id</c> value that asks for every target, in discovery order.</summary>
        public string IdValue => string.Join(',', _ids);

        public void Add(ResourceId id)
        {
            if (_requested.Add(id))
            {
                _ids.Add(id);
            }
        }

        public bool Requested(ResourceId id) => _requested.Contains(id);
    }
}
