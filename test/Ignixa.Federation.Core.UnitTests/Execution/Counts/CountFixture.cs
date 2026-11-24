using Ignixa.Federation.Core.Execution.Counts;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Search;

namespace Ignixa.Federation.Core.UnitTests.Execution.Counts;

/// <summary>Builds real planner plans, capabilities, executors and count-only pages for count execution tests.</summary>
internal static class CountFixture
{
    public static readonly ShardId East = ShardId.Parse("east");

    public static readonly ShardId West = ShardId.Parse("west");

    public static readonly IReadOnlyDictionary<int, string> NoReplacements = new Dictionary<int, string>();

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>East and west, both certifying exactly <paramref name="shapes"/>.</summary>
    public static SearchCapabilities Certified(params string[] shapes)
    {
        SearchCertification certification = TestSearch.Certification(exactCountShapes: shapes);
        return TestSearch.Capabilities(certification, certification);
    }

    /// <summary>Every shard of <paramref name="registry"/>, each certifying exactly <paramref name="shapes"/>.</summary>
    public static SearchCapabilities Certified(ShardRegistry registry, params string[] shapes)
    {
        SearchCertification certification = TestSearch.Certification(exactCountShapes: shapes);
        return new SearchCapabilities(
            registry,
            registry.Shards.Select(s => new ShardSearchCapability(s.Id, TestSearch.AllResources, certification, "4.0.1")), TestSearch.R4Specification);
    }

    public static QueryPlan Plan(SearchCapabilities capabilities, string type, string query) =>
        TestSearch.PlanOk(type, query, TestSearch.Planner(capabilities));

    public static FederatedCountExecutor Executor(
        IShardClient client,
        SearchCapabilities capabilities,
        CountDialect dialect = CountDialect.CountZero,
        int maxParallelCounts = 16) =>
        new(client, capabilities, Dialects(capabilities, dialect), maxParallelCounts);

    public static Dictionary<ShardId, CountDialect> Dialects(SearchCapabilities capabilities, CountDialect dialect) =>
        capabilities.Registry.Shards.ToDictionary(s => s.Id, _ => dialect);

    public static ShardCountTarget[] SearchEvery(SearchCapabilities capabilities, ShardSearchMethod method = ShardSearchMethod.Get) =>
        [.. capabilities.Registry.Shards.Select(s => ShardCountTarget.Search(s.Id, method, NoReplacements))];

    /// <summary>A well-formed count-only response: no entries, no <c>next</c>, the given total.</summary>
    public static ShardPage Count(long? total) => new([], null, total);

    public static void Answer(this FakeShardClient client, ShardId shard, long? total) =>
        client.OnSearch(shard, (_, _) => Task.FromResult(Count(total)));

    /// <summary>Scripts <paramref name="shard"/> to wait for <paramref name="gate"/>, honouring cancellation, then answer.</summary>
    public static void AnswerAfter(this FakeShardClient client, ShardId shard, TaskCompletionSource gate, long total) =>
        client.OnSearch(shard, async (_, cancellationToken) =>
        {
            await gate.Task.WaitAsync(cancellationToken);
            return Count(total);
        });

    /// <summary>Gets the single search sent to <paramref name="shard"/>.</summary>
    public static ShardSearchRequest SentTo(this FakeShardClient client, ShardId shard) =>
        client.Calls.Single(c => c.Shard == shard).Search!;

    /// <summary>Renders the parameters of the single search sent to <paramref name="shard"/> as wire text.</summary>
    public static string WireTo(this FakeShardClient client, ShardId shard) => QueryRewriter.Render(client.SentTo(shard).Parameters);
}
