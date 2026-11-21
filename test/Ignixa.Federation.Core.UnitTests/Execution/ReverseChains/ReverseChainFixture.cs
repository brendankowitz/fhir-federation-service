using Ignixa.Federation.Core.Execution.Counts;
using Ignixa.Federation.Core.Execution.ReverseChains;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Execution.Counts;
using Ignixa.Federation.Core.UnitTests.Execution.Includes;
using Ignixa.Federation.Core.UnitTests.Search;

namespace Ignixa.Federation.Core.UnitTests.Execution.ReverseChains;

internal sealed class ReverseChainFixture
{
    public const string Has = "_has:Observation:subject:code";
    public FakeShardClient Client { get; init; } = new();
    public SearchCapabilities Capabilities { get; init; } = TestSearch.Capabilities();

    /// <summary>Gets the count executor whose requests are measured; defaults to the Microsoft dialect on <see cref="Capabilities"/>.</summary>
    public FederatedCountExecutor? Counts { get; init; }

    /// <summary>Gets the host transport predicate; defaults to admitting every request.</summary>
    public Func<ShardSearchRequest, bool> Fits { get; init; } = _ => true;

    public QueryPlan Plan(string query = Has + "=1", bool native = false) =>
        TestSearch.PlanOk("Patient", query, TestSearch.Planner(Capabilities, native ? ["Observation.subject:Patient"] : []));

    public ReverseChainResolver Resolver(int keys = 1000, int rows = 5000, int parallel = 16, TimeSpan? timeout = null) =>
        new(Client, Capabilities, Counts ?? CountFixture.Executor(Client, Capabilities), Fits, keys, rows, parallel, timeout ?? TimeSpan.FromSeconds(15));

    public Task<ReverseChainResolution> ResolveAsync(QueryPlan plan, int budget = 100) =>
        Resolver().ResolveAsync(plan, IncludeFixture.AllTypes, budget, TestContext.Current.CancellationToken);
}
