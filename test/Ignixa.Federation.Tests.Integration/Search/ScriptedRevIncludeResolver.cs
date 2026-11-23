using System.Collections.Concurrent;
using Ignixa.Federation.Api.Search;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Tests.Integration.Search;

/// <summary>Everything the host handed to one <see cref="IRevIncludePageResolver.ResolveAsync"/> call.</summary>
internal sealed record ResolverCall(
    QueryPlan Plan,
    IReadOnlyList<ShardEntry> Matches,
    IReadOnlySet<ResourceType> Readable,
    IShardClient Client,
    ShardRegistry Registry,
    int MaxParallel,
    Func<ShardSearchRequest, bool> Fits,
    int IncludeLimit,
    int RemainingFetches,
    CancellationToken Token);

/// <summary>
/// A scripted resolver: it records every call and answers from a script instead of traversing anything, so the tests
/// exercise only what the host decides around it. The script may throw, call the received client, or return <see langword="null"/>.
/// </summary>
internal sealed class ScriptedRevIncludeResolver(Func<ResolverCall, Task<RevIncludePageResult?>>? script = null) : IRevIncludePageResolver
{
    private readonly ConcurrentQueue<ResolverCall> _calls = new();

    public IReadOnlyList<ResolverCall> Calls => [.. _calls];

    /// <summary>Gets or sets the script; by default it resolves to one Observation include with no truncation.</summary>
    public Func<ResolverCall, Task<RevIncludePageResult?>> Script { get; set; } =
        script ?? (_ => Task.FromResult<RevIncludePageResult?>(new RevIncludePageResult([Entry("east", "Observation", "obs-dup")], false)));

    public Task<RevIncludePageResult?> ResolveAsync(
        QueryPlan plan,
        IReadOnlyList<ShardEntry> emittedMatches,
        IReadOnlySet<ResourceType> readableTypes,
        IShardClient client,
        ShardRegistry registry,
        int maxParallelFetches,
        Func<ShardSearchRequest, bool> fits,
        int includeLimit,
        int remainingFetches,
        CancellationToken cancellationToken)
    {
        var call = new ResolverCall(plan, emittedMatches, readableTypes, client, registry, maxParallelFetches, fits, includeLimit,
            remainingFetches, cancellationToken);
        _calls.Enqueue(call);
        return Script(call);
    }

    /// <summary>Builds an entry holding the seeded owner's exact bytes.</summary>
    public static ShardEntry Entry(string shard, string type, string id) =>
        new(new ResourceKey(ShardId.Parse(shard), ResourceType.Parse(type), ResourceId.Parse(id)), null, null, SearchWorld.Slice(shard, type, id));

    /// <summary>Builds the one-shot script that returns <paramref name="includes"/>.</summary>
    public static Func<ResolverCall, Task<RevIncludePageResult?>> Returning(bool truncated, params ShardEntry[] includes) =>
        _ => Task.FromResult<RevIncludePageResult?>(new RevIncludePageResult(includes, truncated));
}
