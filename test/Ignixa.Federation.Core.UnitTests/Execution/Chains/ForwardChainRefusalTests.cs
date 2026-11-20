using System.Text;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Execution.Chains;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Search;
using Ignixa.Federation.Core.UnitTests.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Chains.ChainScenario;

namespace Ignixa.Federation.Core.UnitTests.Execution.Chains;

/// <summary>
/// What the resolver refuses or rejects before any witness or primary request leaves the federation: unreadable types,
/// semantics this slice does not execute, requests a shard cannot accept and plans from another registry (design spec §7.5).
/// </summary>
public sealed class ForwardChainRefusalTests
{
    private const string Chain = "subject:Patient.name=Orchid";
    private const string TwoChains = "subject:Patient.name=Orchid&performer:Practitioner.name=Doc";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(Chain, new[] { "Patient", "Practitioner" }, -1)]
    [InlineData(Chain, new[] { "Observation", "Practitioner" }, 0)]
    [InlineData(TwoChains, new[] { "Observation", "Patient" }, 1)]
    public async Task UnreadableSearchedType_IsUnauthorizedBeforeAnyRequest(string query, string[] readable, int ordinal)
    {
        ShardRegistry registry = Registry();
        var backend = new ChainBackend(registry); // Every witness search would be empty.

        ForwardChainResolution result = await Resolver(backend.Client, registry)
            .ResolveAsync(Plan(registry, query), Readable(readable), 100, 1_000_000, Token);

        ShouldRefuse(result, ForwardChainRefusalReason.Unauthorized, ordinal, null);
        backend.Client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnreadablePassthroughTarget_IsUnauthorized()
    {
        ShardRegistry registry = Registry();
        var backend = new ChainBackend(registry);

        ForwardChainResolution result = await Resolver(backend.Client, registry)
            .ResolveAsync(Plan(registry, Chain, "Observation.subject:Patient"), Readable("Observation"), 100, 1_000_000, Token);

        ShouldRefuse(result, ForwardChainRefusalReason.Unauthorized, 0, null);
        backend.Client.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Observation", "code=a&_include=Observation:subject:Patient", ForwardChainRefusalReason.Include, 1)]
    [InlineData("Patient", "name=a&_revinclude=Observation:subject", ForwardChainRefusalReason.RevInclude, 1)]
    [InlineData("Observation", "code=a&_sort=-_lastUpdated", ForwardChainRefusalReason.Sort, 1)]
    [InlineData("Observation", "code=a&_summary=count", ForwardChainRefusalReason.CountOnly, -1)]
    [InlineData("Observation", "code=a&_total=accurate", ForwardChainRefusalReason.Total, -1)]
    public async Task SemanticsThisSliceDoesNotExecute_AreRefusedBeforeAnyRequest(
        string type,
        string query,
        ForwardChainRefusalReason reason,
        int ordinal)
    {
        ShardRegistry registry = TestSearch.Registry();
        var client = new FakeShardClient();
        SearchCertification certification = TestSearch.Certification(exactCountShapes: ["Observation?code"]);
        SearchPlanner planner = TestSearch.Planner(TestSearch.Capabilities(certification, certification, registry: registry));

        ForwardChainResolution result = await Resolver(client, registry).ResolveAsync(TestSearch.PlanOk(type, query, planner), Token);

        ShouldRefuse(result, reason, ordinal, null);
        client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task DistributedReverseChain_IsRefused_AndAPassthroughOneIsSentRaw()
    {
        ShardRegistry registry = TestSearch.Registry();
        var client = new FakeShardClient();
        ForwardChainResolver resolver = Resolver(client, registry);
        SearchCapabilities capabilities = TestSearch.Capabilities(registry: registry);

        ForwardChainResolution distributed = await resolver
            .ResolveAsync(TestSearch.PlanOk("Patient", "_has:Observation:subject:code=a", TestSearch.Planner(capabilities)), Token);
        ForwardChainResolution passthrough = await resolver
            .ResolveAsync(TestSearch.PlanOk("Patient", "_has:Observation:subject:code=a", TestSearch.Planner(capabilities, "Observation.subject:Patient")), Token);

        ShouldRefuse(distributed, ForwardChainRefusalReason.ReverseChain, 0, null);
        passthrough.ShouldBeOfType<ForwardChainReady>().Streams
            .ShouldAllBe(s => s.Parameters.Single() == new ParameterOccurrence("_has:Observation:subject:code", "a"));
        client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task PlanFromAnotherRegistryInstance_IsRejectedBeforeAnyRequest()
    {
        ShardRegistry registry = Registry();
        var backend = new ChainBackend(registry);
        QueryPlan foreign = Plan(Registry(), Chain);

        await Should.ThrowAsync<ArgumentException>(Resolver(backend.Client, registry).ResolveAsync(foreign, Token));
        backend.Client.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task WitnessRequestOverTheShardBound_IsRefusedBeforeAnyRequest(int slack)
    {
        ShardRegistry registry = Registry();
        var backend = new ChainBackend(registry);
        int body = Encoding.UTF8.GetByteCount("name=Orchid&_elements=id&_count=1000");
        ForwardChainResolver resolver = Resolver(
            backend.Client,
            registry,
            bound: s => new ShardRequestBound(ShardSearchMethod.PostSearch, s.Id == West ? body + slack : 1_000_000));

        ForwardChainResolution result = await resolver.ResolveAsync(Plan(registry, Chain), Token);

        if (slack == 0)
        {
            result.ShouldBeOfType<ForwardChainEmpty>();
            backend.Client.Calls.Count.ShouldBe(2);
        }
        else
        {
            ShouldRefuse(result, ForwardChainRefusalReason.RequestTooLarge, 0, West);
            backend.Client.Calls.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PrimaryRequestOverTheShardBound_IsRefusedBeforeItIsSent(int slack)
    {
        ShardRegistry registry = Registry();
        var backend = new ChainBackend(registry).Patient(East, "orchid", "Orchid");
        int target = Encoding.UTF8.GetByteCount("/fhir/Observation?subject=Patient%2Forchid&_count=20");
        ForwardChainResolver resolver = Resolver(
            backend.Client,
            registry,
            bound: s => s.Id == East ? new ShardRequestBound(ShardSearchMethod.Get, target + slack) : new ShardRequestBound(ShardSearchMethod.PostSearch, 1_000_000));

        ForwardChainResolution result = await resolver.ResolveAsync(Plan(registry, Chain), Token);

        backend.Client.Calls.Count.ShouldBe(2);
        if (slack == 0)
        {
            ShardSearchRequest east = result.ShouldBeOfType<ForwardChainReady>().Stream(East);
            (east.Method, east.PageSize).ShouldBe((ShardSearchMethod.Get, 20));
            east.Parameters.ShouldBe([QueryRewriter.Encode("subject", "Patient/orchid")]);
        }
        else
        {
            ShouldRefuse(result, ForwardChainRefusalReason.RequestTooLarge, -1, East);
        }
    }

    [Fact]
    public async Task NegativeBudgets_AreRejected()
    {
        ShardRegistry registry = Registry();
        ForwardChainResolver resolver = Resolver(new FakeShardClient(), registry);
        QueryPlan plan = Plan(registry, Chain);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(resolver.ResolveAsync(plan, Token, fetchBudget: -1));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(resolver.ResolveAsync(plan, Token, payloadBytes: -1));
    }

    [Fact]
    public void BoundsMustCoverExactlyTheRegistryShards()
    {
        ShardRegistry registry = Registry();
        var bound = new ShardRequestBound(ShardSearchMethod.PostSearch, 100);
        var missing = new Dictionary<ShardId, ShardRequestBound> { [East] = bound };
        var extra = new Dictionary<ShardId, ShardRequestBound> { [East] = bound, [West] = bound, [ShardId.Parse("north")] = bound };

        Should.Throw<ArgumentException>(() => new ForwardChainResolver(new FakeShardClient(), registry, missing, new LimitOptions(), new TimeoutOptions()));
        Should.Throw<ArgumentException>(() => new ForwardChainResolver(new FakeShardClient(), registry, extra, new LimitOptions(), new TimeoutOptions()));
    }

    [Fact]
    public void PostSearchBound_RequiresAProfileThatSupportsIt()
    {
        var noPost = new BackendProfile("NoPost", SupportsPostSearch: false, MaxCount: 100, NextLinkRedemption.FollowGet, null);
        ShardRegistry registry = ShardRegistry.Create([TestShards.East().WithProfile("NoPost")], [noPost]);

        Should.Throw<ArgumentException>(() => Resolver(new FakeShardClient(), registry));
        Resolver(new FakeShardClient(), registry, bound: _ => new ShardRequestBound(ShardSearchMethod.Get, 100)).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(nameof(LimitOptions.MaxChainKeys))]
    [InlineData(nameof(LimitOptions.MaxParallelBackendCalls))]
    [InlineData(nameof(TimeoutOptions.ChainSubquery))]
    [InlineData(nameof(TimeoutOptions.BackendCall))]
    public void NonPositiveLimit_IsRejected(string limit)
    {
        var limits = new LimitOptions();
        var timeouts = new TimeoutOptions();
        switch (limit)
        {
            case nameof(LimitOptions.MaxChainKeys): limits.MaxChainKeys = 0; break;
            case nameof(LimitOptions.MaxParallelBackendCalls): limits.MaxParallelBackendCalls = 0; break;
            case nameof(TimeoutOptions.ChainSubquery): timeouts.ChainSubquery = TimeSpan.Zero; break;
            default: timeouts.BackendCall = TimeSpan.Zero; break;
        }

        Should.Throw<ArgumentOutOfRangeException>(() => Resolver(new FakeShardClient(), Registry(), limits, timeouts));
    }

    [Fact]
    public void ShardRequestBound_RequiresADefinedMethodAndAPositiveSize()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ShardRequestBound((ShardSearchMethod)99, 100));
        Should.Throw<ArgumentOutOfRangeException>(() => new ShardRequestBound(ShardSearchMethod.Get, 0));
        new ShardRequestBound(ShardSearchMethod.Get, 1).MaxRequestBytes.ShouldBe(1);
    }

    private static HashSet<ResourceType> Readable(params string[] types) => [.. types.Select(TestSearch.Type)];

    private static void ShouldRefuse(ForwardChainResolution result, ForwardChainRefusalReason reason, int ordinal, ShardId? shard)
    {
        ForwardChainRefusal refusal = result.ShouldBeOfType<ForwardChainRefusal>();
        (refusal.Reason, refusal.Ordinal, refusal.Shard).ShouldBe((reason, ordinal, shard));
    }
}
