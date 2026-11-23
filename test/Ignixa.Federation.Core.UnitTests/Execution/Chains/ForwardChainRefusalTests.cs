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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReverseChain_IsRefusedInEitherMode_AfterAuthorization(bool passthrough)
    {
        ShardRegistry registry = TestSearch.Registry();
        var client = new FakeShardClient();
        ForwardChainResolver resolver = Resolver(client, registry);
        SearchCapabilities capabilities = TestSearch.Capabilities(registry: registry);
        SearchPlanner planner = passthrough ? TestSearch.Planner(capabilities, "Observation.subject:Patient") : TestSearch.Planner(capabilities);
        QueryPlan plan = TestSearch.PlanOk("Patient", "_has:Observation:subject:code=a", planner);
        plan.Occurrences[0].Hop!.Mode.ShouldBe(passthrough ? ChainResolutionMode.Passthrough : ChainResolutionMode.Distributed);

        ForwardChainResolution refused = await resolver.ResolveAsync(plan, Token);
        ForwardChainResolution unauthorized = await resolver.ResolveAsync(plan, Readable("Patient"), 100, 1_000_000, Token);

        ShouldRefuse(refused, ForwardChainRefusalReason.ReverseChain, 0, null);
        ShouldRefuse(unauthorized, ForwardChainRefusalReason.Unauthorized, 0, null);
        client.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)] // Every witness key is west's own, which west can spell only by a link base alias it does not have.
    [InlineData(true)] // East's foreign key still spells on west; west's own key must not silently drop out of the OR list.
    public async Task OwnKeyWithNoCertifiedSpellingOnItsOwner_IsRefusedForThatShard(bool alsoAnEastKey)
    {
        ShardRegistry registry = Registry();
        registry.Shards[1].CanonicalLinkBaseAliases.ShouldBeEmpty();
        var backend = new ChainBackend(registry).Patient(West, "w1", "Orchid");
        if (alsoAnEastKey)
        {
            backend.Patient(East, "orchid", "Orchid");
        }

        ForwardChainResolution result = await Resolver(backend.Client, registry)
            .ResolveAsync(Plan(registry, ReferenceSearchForms.LinkBaseAliases, Chain), Token);

        ShouldRefuse(result, ForwardChainRefusalReason.UnspellableReference, 0, West);
        result.RemainingFetchBudget.ShouldBe(98);
        backend.Client.Calls.Count.ShouldBe(2);
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
            bound: s => s.Id == West ? new ShardRequestBound(1_000_000, body + slack) : ProfileBound(s));

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
        ShardRegistry registry = GetOnlyEastRegistry();
        var backend = new ChainBackend(registry).Patient(East, "orchid", "Orchid");
        int target = Encoding.UTF8.GetByteCount("/fhir/Observation?subject=Patient%2Forchid&_count=20");
        ForwardChainResolver resolver = Resolver(
            backend.Client,
            registry,
            bound: s => s.Id == East ? new ShardRequestBound(target + slack) : ProfileBound(s));

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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task EqualPairPrimaryRequestOverTheGetCeiling_IsRefusedEvenWhenItsPostBodyWouldFit(int slack)
    {
        ShardRegistry registry = Registry();
        var backend = new ChainBackend(registry).Patient(East, "orchid", "Orchid");
        // The raw pipe is sent as one byte: the request target is the verbatim text, not an escaped form of it.
        int target = Encoding.UTF8.GetByteCount("/fhir/Observation?code=a|b&subject=Patient%2Forchid&subject=Patient%2Forchid&_count=20");
        ForwardChainResolver resolver = Resolver(
            backend.Client,
            registry,
            bound: s => s.Id == East ? new ShardRequestBound(target + slack, 1_000_000) : ProfileBound(s));

        ForwardChainResolution result = await resolver.ResolveAsync(Plan(registry, $"code=a|b&{Chain}&{Chain}"), Token);

        backend.Client.Calls.Count.ShouldBe(4); // Two chains, each searched on both shards.
        if (slack == 0)
        {
            // East's target fits exactly; west's longer foreign spelling fits its large ceiling.
            ShardSearchRequest east = result.ShouldBeOfType<ForwardChainReady>().Stream(East);
            (east.Method, east.PageSize).ShouldBe((ShardSearchMethod.Get, 20));
        }
        else
        {
            ShouldRefuse(result, ForwardChainRefusalReason.RequestTooLarge, -1, East);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task EqualPairPrimaryRequestWithEscapedUnreservedSpelling_IsMeasuredVerbatim(int slack)
    {
        ShardRegistry registry = Registry();
        var backend = new ChainBackend(registry).Patient(East, "orchid", "Orchid");
        // `c%6Fde=a%62c` and `code=abc` are equal after one decode, so east is sent GET. A canonicalizing target would
        // decode the escapes and measure four bytes less than the text on the wire.
        const string EscapedCode = "c%6Fde=a%62c";
        int target = Encoding.UTF8.GetByteCount($"/fhir/Observation?{EscapedCode}&subject=Patient%2Forchid&code=abc&_count=20");
        ForwardChainResolver resolver = Resolver(
            backend.Client,
            registry,
            bound: s => s.Id == East ? new ShardRequestBound(target + slack, 1_000_000) : ProfileBound(s));

        ForwardChainResolution result = await resolver.ResolveAsync(Plan(registry, $"{EscapedCode}&{Chain}&code=abc"), Token);

        backend.Client.Calls.Count.ShouldBe(2); // The one chain's witness search on each shard; nothing else is called.
        backend.Client.Calls.ShouldAllBe(c => c.Search!.Type == Patient);
        if (slack == 0)
        {
            ShardSearchRequest east = result.ShouldBeOfType<ForwardChainReady>().Stream(East);
            east.Method.ShouldBe(ShardSearchMethod.Get);
            east.Parameters.ShouldBe([new ParameterOccurrence("c%6Fde", "a%62c"), QueryRewriter.Encode("subject", "Patient/orchid"), new ParameterOccurrence("code", "abc")]);
        }
        else
        {
            ShouldRefuse(result, ForwardChainRefusalReason.RequestTooLarge, -1, East);
        }
    }

    [Fact]
    public async Task EachShardIsMeasuredAgainstTheCeilingOfItsOwnMethod()
    {
        ShardRegistry registry = GetOnlyEastRegistry();
        var backend = new ChainBackend(registry).Patient(East, "orchid", "Orchid");
        Func<ShardDescriptor, ShardRequestBound> tinyGetOnWest = s => s.Id == West ? new ShardRequestBound(1, 1_000_000) : ProfileBound(s);

        // West goes by POST for distinct pairs, so its tiny GET ceiling is irrelevant; equal pairs send it by GET and exceed it.
        ForwardChainResolution distinct = await Resolver(backend.Client, registry, bound: tinyGetOnWest).ResolveAsync(Plan(registry, Chain), Token);
        ForwardChainResolution equal = await Resolver(backend.Client, registry, bound: tinyGetOnWest).ResolveAsync(Plan(registry, $"{Chain}&{Chain}"), Token);

        distinct.ShouldBeOfType<ForwardChainReady>().Stream(West).Method.ShouldBe(ShardSearchMethod.PostSearch);
        ShouldRefuse(equal, ForwardChainRefusalReason.RequestTooLarge, -1, West);
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
        var bound = new ShardRequestBound(100, 100);
        var missing = new Dictionary<ShardId, ShardRequestBound> { [East] = bound };
        var extra = new Dictionary<ShardId, ShardRequestBound> { [East] = bound, [West] = bound, [ShardId.Parse("north")] = bound };

        Should.Throw<ArgumentException>(() => new ForwardChainResolver(new FakeShardClient(), registry, missing, new LimitOptions(), new TimeoutOptions()));
        Should.Throw<ArgumentException>(() => new ForwardChainResolver(new FakeShardClient(), registry, extra, new LimitOptions(), new TimeoutOptions()));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)] // A POST-capable shard needs a POST ceiling: equal pairs go by GET, the rest by POST.
    [InlineData(false, false, true)]
    [InlineData(false, true, false)] // A GET-only shard is never sent POST, so a POST ceiling is a configuration mistake.
    public void PostCeiling_IsRequiredExactlyWhenTheShardsProfileSupportsPostSearch(bool postCapable, bool givenPostCeiling, bool accepted)
    {
        ShardRegistry registry = postCapable ? Registry() : GetOnlyEastRegistry();
        Func<ShardDescriptor, ShardRequestBound> bound = s => s.Id == East ? new ShardRequestBound(100, givenPostCeiling ? 100 : null) : ProfileBound(s);

        if (accepted)
        {
            Resolver(new FakeShardClient(), registry, bound: bound).ShouldNotBeNull();
        }
        else
        {
            Should.Throw<ArgumentException>(() => Resolver(new FakeShardClient(), registry, bound: bound)).ParamName.ShouldBe("requestBounds");
        }
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
    public void ShardRequestBound_RequiresPositiveCeilings_AndTheOptionalPostCeilingIsOptional()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ShardRequestBound(0)).ParamName.ShouldBe("maxGetTargetBytes");
        Should.Throw<ArgumentOutOfRangeException>(() => new ShardRequestBound(1, 0)).ParamName.ShouldBe("maxPostBodyBytes");
        Should.Throw<ArgumentOutOfRangeException>(() => new ShardRequestBound(1, -1));
        new ShardRequestBound(1).MaxPostBodyBytes.ShouldBeNull();
        new ShardRequestBound(1, 2).MaxPostBodyBytes.ShouldBe(2);
    }

    private static HashSet<ResourceType> Readable(params string[] types) => [.. types.Select(TestSearch.Type)];

    private static void ShouldRefuse(ForwardChainResolution result, ForwardChainRefusalReason reason, int ordinal, ShardId? shard)
    {
        ForwardChainRefusal refusal = result.ShouldBeOfType<ForwardChainRefusal>();
        (refusal.Reason, refusal.Ordinal, refusal.Shard).ShouldBe((reason, ordinal, shard));
    }
}
