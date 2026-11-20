using Ignixa.Federation.Core.Execution.ReverseChains;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.UnitTests.Execution.Includes;
using Ignixa.Federation.Core.UnitTests.Search;

namespace Ignixa.Federation.Core.UnitTests.Execution.ReverseChains;

internal sealed class ReverseChainFixture
{
    public const string Has = "_has:Observation:subject:code";
    public FakeShardClient Client { get; } = new();
    public SearchCapabilities Capabilities { get; init; } = TestSearch.Capabilities();

    public QueryPlan Plan(string query = Has + "=1", bool native = false) =>
        TestSearch.PlanOk("Patient", query, TestSearch.Planner(Capabilities, native ? ["Observation.subject:Patient"] : []));

    public ReverseChainResolver Resolver(int keys = 1000, int rows = 5000, int parallel = 16, TimeSpan? timeout = null) =>
        new(Client, Capabilities, keys, rows, parallel, timeout ?? TimeSpan.FromSeconds(15));

    public Task<ReverseChainResolution> ResolveAsync(QueryPlan plan, int budget = 100) =>
        Resolver().ResolveAsync(plan, IncludeFixture.AllTypes, budget, TestContext.Current.CancellationToken);
}
