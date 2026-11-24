using System.Text;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.UnitTests.Search;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Chains;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Chains.ChainScenario;

namespace Ignixa.Federation.Core.UnitTests.Execution.Chains;

/// <summary>
/// The host's pure fit predicate (D80 Δ2): the resolver hands it every request exactly as first sent, witnesses before any
/// I/O and primaries after the witness calls, and refuses on its verdict alone. The mixed registry sends east GET only
/// (MaxCount 500) and west by POST with MaxCount 10, so the federation page size of 20 is clamped on west alone.
/// </summary>
public sealed class ForwardChainHostFitTests
{
    private const string Chain = "subject:Patient.name=Orchid";
    private const string RawPair = "code=a%20b"; // Sent verbatim, never re-encoded as code=a+b.
    private const string EastOrchidCanonical = "https%3A%2F%2Feast.example%2Ffhir%2FPatient%2Forchid";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Predicate_SeesEveryFinalizedRequest_WitnessesBeforeAnyCall_PrimariesWithTheirMethodAndClampedCount()
    {
        ShardRegistry registry = MixedRegistry();
        var backend = new ChainBackend(registry).Patient(East, "orchid", "Orchid");
        var seen = new List<(ShardSearchRequest Request, int CallsSoFar)>();

        ForwardChainResolution result = await Resolver(backend.Client, registry, r =>
        {
            seen.Add((r, backend.Client.Calls.Count));
            return true;
        }).ResolveAsync(Plan(registry, $"{RawPair}&{Chain}"), Token);

        ParameterOccurrence[] witness = [new("name", "Orchid"), new("_elements", "id")];
        ParameterOccurrence raw = new("code", "a%20b");
        seen.Select(s => s.CallsSoFar).ShouldBe([0, 0, 2, 2]);
        ShouldBe(seen[0].Request, East, Patient, witness, 500, ShardSearchMethod.Get);
        ShouldBe(seen[1].Request, West, Patient, witness, 10, ShardSearchMethod.PostSearch);
        ShouldBe(seen[2].Request, East, Observation, [raw, new("subject", "Patient%2Forchid")], 20, ShardSearchMethod.Get);
        ShouldBe(seen[3].Request, West, Observation, [raw, new("subject", EastOrchidCanonical)], 10, ShardSearchMethod.PostSearch);

        // The witness requests measured are the ones sent, regardless of which shard starts first.
        var calls = backend.Client.Calls;
        calls.Count.ShouldBe(2);
        for (int i = 0; i < 2; i++)
        {
            ShardSearchRequest measured = seen[i].Request;
            ShardSearchRequest sent = calls.Single(call => call.Shard == measured.Shard).Search.ShouldNotBeNull();
            ShouldBe(sent, measured.Shard, measured.Type, measured.Parameters, measured.PageSize, measured.Method);
        }

        // The templates keep the federation page size; only the measured first send is clamped.
        ForwardChainReady ready = result.ShouldBeOfType<ForwardChainReady>();
        ShouldBe(ready.Stream(East), East, Observation, seen[2].Request.Parameters, 20, ShardSearchMethod.Get);
        ShouldBe(ready.Stream(West), West, Observation, seen[3].Request.Parameters, 20, ShardSearchMethod.PostSearch);
    }

    [Fact]
    public async Task EqualPairs_AreMeasuredAsTheGetTheyAreSentAs()
    {
        ShardRegistry registry = MixedRegistry();
        var backend = new ChainBackend(registry).Patient(East, "orchid", "Orchid");
        var primaries = new List<ShardSearchRequest>();

        ForwardChainResolution result = await Resolver(backend.Client, registry, r =>
        {
            if (r.Type == Observation)
            {
                primaries.Add(r);
            }

            return true;
        }).ResolveAsync(Plan(registry, $"code=abc&{Chain}&code=abc"), Token);

        result.ShouldBeOfType<ForwardChainReady>();
        primaries.Select(p => (p.Shard, p.Method, p.PageSize)).ShouldBe([(East, ShardSearchMethod.Get, 20), (West, ShardSearchMethod.Get, 10)]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ClampedPostBody_AtTheExactCeiling_IsAccepted_AndOneByteUnderIsRefused(int slack)
    {
        ShardRegistry registry = MixedRegistry();
        var backend = new ChainBackend(registry).Patient(East, "orchid", "Orchid");
        int ceiling = Encoding.UTF8.GetByteCount($"{RawPair}&subject={EastOrchidCanonical}&_count=10") + slack;

        ForwardChainResolution result = await Resolver(backend.Client, registry, r =>
            r.Shard != West || r.Type != Observation || Encoding.UTF8.GetByteCount($"{QueryRewriter.Render(r.Parameters)}&_count={r.PageSize}") <= ceiling)
            .ResolveAsync(Plan(registry, $"{RawPair}&{Chain}"), Token);

        backend.Client.Calls.Count.ShouldBe(2);
        if (slack == 0)
        {
            result.ShouldBeOfType<ForwardChainReady>().Streams.Count.ShouldBe(2);
        }
        else
        {
            ShouldRefuse(result, ForwardChainRefusalReason.RequestTooLarge, -1, West);
        }
    }

    [Fact]
    public async Task LaterWitnessRefusal_IsBeforeAnyCall_OnTheFinalizedWitnessRequest()
    {
        ShardRegistry registry = MixedRegistry();
        var backend = new ChainBackend(registry).Patient(East, "orchid", "Orchid");
        var seen = new List<ShardSearchRequest>();

        ForwardChainResolution result = await Resolver(backend.Client, registry, r =>
        {
            seen.Add(r);
            return !(r.Shard == West && r.Type == Practitioner);
        }).ResolveAsync(Plan(registry, $"{Chain}&performer:Practitioner.name=Doc"), Token);

        ShouldRefuse(result, ForwardChainRefusalReason.RequestTooLarge, 1, West);
        backend.Client.Calls.ShouldBeEmpty();
        seen.Count.ShouldBe(4); // Both shards for the first chain, then east and west for the second.
        ShouldBe(seen[^1], West, Practitioner, [new("name", "Doc"), new("_elements", "id")], 10, ShardSearchMethod.PostSearch);
    }

    [Theory]
    [InlineData("east")]
    [InlineData("west")] // East's primary was built and accepted, but no partial set of streams is exposed.
    public async Task PrimaryRefusal_FollowsTheWitnessCalls_AndExposesNoStream(string refused)
    {
        ShardRegistry registry = MixedRegistry();
        var backend = new ChainBackend(registry).Patient(East, "orchid", "Orchid");
        ShardId shard = ShardId.Parse(refused);

        ForwardChainResolution result = await Resolver(backend.Client, registry, r => !(r.Shard == shard && r.Type == Observation))
            .ResolveAsync(Plan(registry, Chain), Token);

        backend.Client.Calls.Count.ShouldBe(2);
        ShouldRefuse(result, ForwardChainRefusalReason.RequestTooLarge, -1, shard);
        result.RemainingFetchBudget.ShouldBe(98);
    }

    [Fact]
    public void HostPredicateConstructor_RejectsMissingArguments_AndNonPositiveLimits()
    {
        ShardRegistry registry = Registry();
        var client = new FakeShardClient();
        Func<ShardSearchRequest, bool> fits = _ => true;

        Should.Throw<ArgumentNullException>(() => new ForwardChainResolver(null!, registry, TestSearch.R4Specification, new LimitOptions(), new TimeoutOptions(), fits)).ParamName.ShouldBe("client");
        Should.Throw<ArgumentNullException>(() => new ForwardChainResolver(client, null!, TestSearch.R4Specification, new LimitOptions(), new TimeoutOptions(), fits)).ParamName.ShouldBe("registry");
        Should.Throw<ArgumentNullException>(() => new ForwardChainResolver(client, registry, null!, new LimitOptions(), new TimeoutOptions(), fits)).ParamName.ShouldBe("specification");
        Should.Throw<ArgumentNullException>(() => new ForwardChainResolver(client, registry, TestSearch.R4Specification, null!, new TimeoutOptions(), fits)).ParamName.ShouldBe("limits");
        Should.Throw<ArgumentNullException>(() => new ForwardChainResolver(client, registry, TestSearch.R4Specification, new LimitOptions(), null!, fits)).ParamName.ShouldBe("timeouts");
        Should.Throw<ArgumentNullException>(() => new ForwardChainResolver(client, registry, TestSearch.R4Specification, new LimitOptions(), new TimeoutOptions(), null!)).ParamName.ShouldBe("fitsRequest");
        Should.Throw<ArgumentOutOfRangeException>(() => new ForwardChainResolver(client, registry, TestSearch.R4Specification, new LimitOptions { MaxChainKeys = 0 }, new TimeoutOptions(), fits));
        Should.Throw<ArgumentOutOfRangeException>(() => new ForwardChainResolver(client, registry, TestSearch.R4Specification, new LimitOptions(), new TimeoutOptions { BackendCall = TimeSpan.Zero }, fits));
    }

    private static void ShouldBe(
        ShardSearchRequest actual,
        ShardId shard,
        ResourceType type,
        IReadOnlyList<ParameterOccurrence> parameters,
        int pageSize,
        ShardSearchMethod method)
    {
        (actual.Shard, actual.Type, actual.PageSize, actual.Method).ShouldBe((shard, type, pageSize, method));
        actual.Parameters.ShouldBe(parameters);
    }

    private static void ShouldRefuse(ForwardChainResolution result, ForwardChainRefusalReason reason, int ordinal, ShardId? shard)
    {
        ForwardChainRefusal refusal = result.ShouldBeOfType<ForwardChainRefusal>();
        (refusal.Reason, refusal.Ordinal, refusal.Shard).ShouldBe((reason, ordinal, shard));
    }
}
