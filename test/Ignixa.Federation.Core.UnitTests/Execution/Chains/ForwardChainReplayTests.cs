using System.Text;
using Ignixa.Federation.Core.Execution.Chains;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Search;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Chains.ChainScenario;

namespace Ignixa.Federation.Core.UnitTests.Execution.Chains;

/// <summary>
/// Rebuilding one distributed forward chain's frozen primary requests without I/O (D80 Δ1). Expected wire text is written
/// out by hand from the scenario's certified forms, so the tests do not share the renderer they check. Replay recovers the
/// witness keys from the first stream only; the caller's exact comparison of every stream is what rejects drift, so a
/// structurally valid but inconsistent or non-canonical frozen set rebuilds to streams that differ from it.
/// </summary>
public sealed class ForwardChainReplayTests
{
    private const string Chain = "subject:Patient.name=Orchid";

    // Standard registry, witnesses east/orchid and west/w1. East: own relative, foreign canonical. West: own canonical,
    // foreign canonical then east's alias, whose ',' and '$' are FHIR-escaped before percent encoding.
    private const string EastOrchidW1 = "Patient%2Forchid%2Chttps%3A%2F%2Fwest.example%2Ffhir%2FPatient%2Fw1";
    private const string WestOrchidW1 =
        "https%3A%2F%2Feast.example%2Ffhir%2FPatient%2Forchid%2Chttps%3A%2F%2Feast-alias.example%2Ffhir%5C%2Cv%5C%242%2FPatient%2Forchid"
        + "%2Chttps%3A%2F%2Fwest.example%2Ffhir%2FPatient%2Fw1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> ReadyQueries => new()
    {
        Chain,
        $"code=a&code=b&{Chain}",
        $"code=a%20b&{Chain}&code=a%20b",
        $"{Chain}&_count=7",
    };

    [Theory]
    [MemberData(nameof(ReadyQueries))]
    public async Task ResolvedStreams_RebuildExactly(string query)
    {
        ShardRegistry registry = Registry();
        var backend = new ChainBackend(registry).Patient(East, "orchid", "Orchid").Patient(West, "w1", "Orchid");
        QueryPlan plan = Plan(registry, query);
        var ready = (ForwardChainReady)await Resolver(backend.Client, registry).ResolveAsync(plan, Token);
        int calls = backend.Client.Calls.Count;

        ShouldBeExactly(Rebuilt(plan, ready.Streams), ready.Streams);
        backend.Client.Calls.Count.ShouldBe(calls);
    }

    [Fact]
    public void HandWrittenStreams_RebuildToTheSameFieldsInRegistryOrder()
    {
        ShardRegistry registry = Registry();
        ShardSearchRequest[] frozen = [Post(East, Subject(EastOrchidW1)), Post(West, Subject(WestOrchidW1))];

        ShouldBeExactly(Rebuilt(Plan(registry, Chain), frozen), frozen);
    }

    [Fact]
    public void TwoOwnersOfTheSameId_AreDistinctKeys()
    {
        ShardRegistry registry = Registry();
        ShardSearchRequest[] frozen =
        [
            Post(East, Subject("Patient%2Fdup%2Chttps%3A%2F%2Fwest.example%2Ffhir%2FPatient%2Fdup")),
            Post(
                West,
                Subject(
                    "https%3A%2F%2Feast.example%2Ffhir%2FPatient%2Fdup%2Chttps%3A%2F%2Feast-alias.example%2Ffhir%5C%2Cv%5C%242%2FPatient%2Fdup"
                    + "%2Chttps%3A%2F%2Fwest.example%2Ffhir%2FPatient%2Fdup")),
        ];

        ShouldBeExactly(Rebuilt(Plan(registry, Chain), frozen), frozen);
        ForwardChainReplay.TryRebuild(Plan(registry, Chain), frozen, 1, _ => true, out _).ShouldBeFalse();
    }

    [Fact]
    public void FirstStreamWithCanonicalAndEscapedAliasForms_AndAdjacentRawPredicates_RebuildExactly()
    {
        // West first: its stream, the one keys are recovered from, holds only absolute forms, including east's escaped alias.
        ShardRegistry registry = WestFirstRegistry();
        ParameterOccurrence code = new("code", "a%20b");
        ShardSearchRequest[] frozen =
        [
            Get(
                West,
                code,
                Subject(
                    "https%3A%2F%2Fwest.example%2Ffhir%2FPatient%2Fw1%2Chttps%3A%2F%2Feast.example%2Ffhir%2FPatient%2Forchid"
                    + "%2Chttps%3A%2F%2Feast-alias.example%2Ffhir%5C%2Cv%5C%242%2FPatient%2Forchid"),
                code),
            Get(East, code, Subject("https%3A%2F%2Fwest.example%2Ffhir%2FPatient%2Fw1%2CPatient%2Forchid"), code),
        ];

        ShouldBeExactly(Rebuilt(Plan(registry, $"code=a%20b&{Chain}&code=a%20b"), frozen), frozen);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public void KeyCap_CountsDistinctPhysicalKeys(int maxChainKeys, bool rebuilt)
    {
        ShardRegistry registry = Registry();
        ShardSearchRequest[] frozen = [Post(East, Subject(EastOrchidW1)), Post(West, Subject(WestOrchidW1))];

        ForwardChainReplay.TryRebuild(Plan(registry, Chain), frozen, maxChainKeys, _ => true, out IReadOnlyList<ShardSearchRequest>? streams).ShouldBe(rebuilt);
        (streams is not null).ShouldBe(rebuilt);
    }

    [Theory]
    [InlineData(2, true)] // Three alternatives, two physical keys: the alias spelling is not a third key.
    [InlineData(1, false)]
    public void KeyCap_CountsKeysNotAlternatives(int maxChainKeys, bool rebuilt)
    {
        ShardRegistry registry = WestFirstRegistry();
        ShardSearchRequest[] frozen =
        [
            Post(
                West,
                Subject(
                    "https%3A%2F%2Fwest.example%2Ffhir%2FPatient%2Fw1%2Chttps%3A%2F%2Feast.example%2Ffhir%2FPatient%2Forchid"
                    + "%2Chttps%3A%2F%2Feast-alias.example%2Ffhir%5C%2Cv%5C%242%2FPatient%2Forchid")),
            Post(East, Subject("https%3A%2F%2Fwest.example%2Ffhir%2FPatient%2Fw1%2CPatient%2Forchid")),
        ];

        ForwardChainReplay.TryRebuild(Plan(registry, Chain), frozen, maxChainKeys, _ => true, out IReadOnlyList<ShardSearchRequest>? streams).ShouldBe(rebuilt);
        (streams is not null).ShouldBe(rebuilt);
    }

    [Fact]
    public void KeyCap_StopsAtTheFirstKeyPastTheCap_BeforeTheRestOfTheListIsRead()
    {
        // The malformed third alternative is never reached; either way the answer is false, and no output is produced.
        ShardRegistry registry = Registry();
        ShardSearchRequest[] frozen = [Post(East, Encoded("Patient/a,Patient/b,Patient/c\\x")), Post(West, Subject(WestOrchidW1))];

        ShouldNotRebuild(Plan(registry, Chain), frozen, maxChainKeys: 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",Patient/orchid")]
    [InlineData("Patient/orchid,")]
    [InlineData("Patient/orchid,,Patient/w2")]
    [InlineData("Patient/or\\xchid")] // Not one of the FHIR escapes \, \| \$ \\.
    [InlineData("Patient/orchid\\")] // A trailing escape character escapes nothing.
    [InlineData("https://east-alias.example/fhir\\,v$2/Patient/orchid")] // The renderer always escapes '$'.
    [InlineData("Patient/or|chid")] // ... and '|'.
    [InlineData("Patient")]
    [InlineData("#p1")]
    [InlineData("https://north.example/fhir/Patient/orchid")] // Not under any registered base or alias.
    [InlineData("http://east.example/fhir/Patient/orchid")]
    [InlineData("Practitioner/orchid")] // Wrong target type for subject:Patient.
    [InlineData("https://west.example/fhir/Practitioner/w1")]
    [InlineData("Patient/orchid/_history/1")] // Versioned.
    [InlineData("https://west.example/fhir/Patient/w1/_history/2")]
    [InlineData("Patient/orchid?x=1")]
    public void MalformedOrUnroutableAlternative_RebuildsNothing(string decoded)
    {
        ShardRegistry registry = Registry();
        ShardSearchRequest[] frozen = [Post(East, Encoded(decoded)), Post(West, Subject(WestOrchidW1))];

        ShouldNotRebuild(Plan(registry, Chain), frozen);
    }

    [Theory]
    [InlineData("Patient%2Forchid%26x%3D1")] // One pair whose decoded id holds '&' and '='.
    [InlineData("Patient%ZZorchid")] // Not a valid escape.
    [InlineData("Patient%2Forchid+1")] // '+' is a space after one form decode, which no reference holds.
    [InlineData("https%3A%2F%2Feast-alias.example%2Ffhir%255C%252Cv%255C%25242%2FPatient%2Forchid")] // Decoded once only.
    public void WireValueThatDoesNotDecodeOnceToAReference_RebuildsNothing(string rawValue)
    {
        ShardRegistry registry = Registry();
        ShardSearchRequest[] frozen = [Post(East, Subject(rawValue)), Post(West, Subject(WestOrchidW1))];

        ShouldNotRebuild(Plan(registry, Chain), frozen);
    }

    [Fact]
    public void GeneratedPairWithAnotherKey_RebuildsNothing()
    {
        ShardRegistry registry = Registry();
        ShardSearchRequest[] frozen = [Post(East, new ParameterOccurrence("performer", EastOrchidW1)), Post(West, Subject(WestOrchidW1))];

        ShouldNotRebuild(Plan(registry, Chain), frozen);
    }

    [Fact]
    public void GeneratedKeyEscapedDifferently_IsRecoveredButFailsExactEquality()
    {
        ShardRegistry registry = Registry();
        ShardSearchRequest[] frozen = [Post(East, new ParameterOccurrence("%73ubject", EastOrchidW1)), Post(West, Subject(WestOrchidW1))];

        IReadOnlyList<ShardSearchRequest> rebuilt = Rebuilt(Plan(registry, Chain), frozen);

        IsExactly(rebuilt, frozen).ShouldBeFalse();
        rebuilt[0].Parameters.ShouldBe([Subject(EastOrchidW1)]);
    }

    public static TheoryData<string, string[]> UnsupportedPlans => new()
    {
        { Chain, ["Observation.subject:Patient"] }, // Passthrough: nothing is replaced.
        { $"{Chain}&{Chain}", [] },
        { $"{Chain}&code=a%2Cb&performer:Practitioner.name=Doc", [] },
        { "code=a", [] },
    };

    [Theory]
    [MemberData(nameof(UnsupportedPlans))]
    public void PlanOutsideTheSingleDistributedChainShape_RebuildsNothing(string query, string[] passthrough)
    {
        ShardRegistry registry = Registry();
        QueryPlan plan = Plan(registry, query, passthrough);
        ShardSearchRequest[] frozen =
        [
            new(East, Observation, plan.Query.Occurrences.Select(_ => Subject(EastOrchidW1)), 20, ShardSearchMethod.PostSearch),
            new(West, Observation, plan.Query.Occurrences.Select(_ => Subject(WestOrchidW1)), 20, ShardSearchMethod.PostSearch),
        ];

        ShouldNotRebuild(plan, frozen);
    }

    [Theory]
    [InlineData("Observation", $"{Chain}&_include=Observation:subject:Patient")]
    [InlineData("Observation", $"{Chain}&_sort=-_lastUpdated")]
    [InlineData("Observation", $"{Chain}&_summary=count")]
    [InlineData("Observation", $"{Chain}&_total=accurate")]
    [InlineData("Patient", "_has:Observation:subject:code=a")] // Replaced too, but by _id.
    public void PlanWithSemanticsReplayDoesNotCover_RebuildsNothing(string type, string query)
    {
        ShardRegistry registry = TestSearch.Registry();
        string standalone = TestSearch.StandaloneShape(TestSearch.ObservationSubjectPatient);
        ReferencePathCertification subject = TestSearch.FullPath(
            TestSearch.ObservationSubjectPatient,
            forwardShapes: [standalone, $"{standalone}&_sort=-_lastUpdated", $"count:{standalone}"]);
        SearchCertification certification = TestSearch.Certification(
            exactCountShapes: ["Observation?subject=,"],
            paths: [subject, .. TestSearch.AllPaths.Skip(1)]);
        QueryPlan plan = TestSearch.PlanOk(type, query, TestSearch.Planner(TestSearch.Capabilities(certification, certification, registry: registry)));
        ShardSearchRequest[] frozen =
        [
            new(East, plan.Type, [Subject("Patient%2Forchid")], plan.PageSize ?? 0, ShardSearchMethod.PostSearch),
            new(West, plan.Type, [Subject("https%3A%2F%2Feast.example%2Ffhir%2FPatient%2Forchid")], plan.PageSize ?? 0, ShardSearchMethod.PostSearch),
        ];

        ShouldNotRebuild(plan, frozen);
    }

    public static TheoryData<string> StructuralMismatches => new()
    {
        "none",
        "one",
        "swapped",
        "foreign",
        "type",
        "pageSize",
        "extraParameter",
        "missingParameter",
        "null",
    };

    [Theory]
    [MemberData(nameof(StructuralMismatches))]
    public void FrozenSetThatIsNotOneTemplatePerRegistryShard_RebuildsNothing(string mismatch)
    {
        ShardRegistry registry = Registry();
        ShardSearchRequest east = Post(East, Subject(EastOrchidW1));
        ShardSearchRequest west = Post(West, Subject(WestOrchidW1));
        ShardSearchRequest[] frozen = mismatch switch
        {
            "none" => [],
            "one" => [east],
            "swapped" => [west, east],
            "foreign" => [east, new(ShardId.Parse("north"), Observation, west.Parameters, 20, ShardSearchMethod.PostSearch)],
            "type" => [east, new(West, Patient, west.Parameters, 20, ShardSearchMethod.PostSearch)],
            "pageSize" => [east, new(West, Observation, west.Parameters, 21, ShardSearchMethod.PostSearch)],
            "extraParameter" => [east, Post(West, Subject(WestOrchidW1), new ParameterOccurrence("code", "a"))],
            "missingParameter" => [east, Post(West)],
            _ => [east, null!],
        };

        ShouldNotRebuild(Plan(registry, Chain), frozen);
    }

    [Fact]
    public void SecondStreamMissingAKey_RebuildsFromTheFirst_AndFailsExactEquality()
    {
        ShardRegistry registry = Registry();
        ShardSearchRequest[] frozen =
        [
            Post(East, Subject(EastOrchidW1)),
            Post(West, Subject("https%3A%2F%2Feast.example%2Ffhir%2FPatient%2Forchid%2Chttps%3A%2F%2Feast-alias.example%2Ffhir%5C%2Cv%5C%242%2FPatient%2Forchid")),
        ];

        IReadOnlyList<ShardSearchRequest> rebuilt = Rebuilt(Plan(registry, Chain), frozen);

        IsExactly(rebuilt, frozen).ShouldBeFalse();
        rebuilt[1].Parameters.ShouldBe([Subject(WestOrchidW1)]);
    }

    [Theory]
    [InlineData("https%3A%2F%2Fwest.example%2Ffhir%2FPatient%2Fw1%2CPatient%2Forchid")] // Not owner-ordinal order.
    [InlineData("Patient%2Forchid%2CPatient%2Forchid%2Chttps%3A%2F%2Fwest.example%2Ffhir%2FPatient%2Fw1")] // A repeated key.
    [InlineData("Patient%2Forchid%2Chttps%3A%2F%2Fwest.example%2Ffhir%2FPatient%2Fw1%2Chttps%3A%2F%2Feast.example%2Ffhir%2FPatient%2Forchid")]
    public void NonCanonicalFirstStream_RebuildsCanonically_AndFailsExactEquality(string eastValue)
    {
        ShardRegistry registry = Registry();
        ShardSearchRequest[] frozen = [Post(East, Subject(eastValue)), Post(West, Subject(WestOrchidW1))];

        IReadOnlyList<ShardSearchRequest> rebuilt = Rebuilt(Plan(registry, Chain), frozen);

        IsExactly(rebuilt, frozen).ShouldBeFalse();
        ShouldBeExactly(rebuilt, [Post(East, Subject(EastOrchidW1)), Post(West, Subject(WestOrchidW1))]);
    }

    [Theory]
    [InlineData("method")]
    [InlineData("predicate")]
    public void OneTamperedStreamOutsideTheGeneratedPair_RebuildsTheOriginal_AndFailsExactEquality(string tamper)
    {
        ShardRegistry registry = Registry();
        QueryPlan plan = Plan(registry, $"code=a&code=b&{Chain}");
        ShardSearchRequest[] original =
        [
            Post(East, new("code", "a"), new("code", "b"), Subject(EastOrchidW1)),
            Post(West, new("code", "a"), new("code", "b"), Subject(WestOrchidW1)),
        ];
        ShardSearchRequest[] frozen = tamper == "method"
            ? [original[0], new(West, Observation, original[1].Parameters, 20, ShardSearchMethod.Get)]
            : [original[0], Post(West, new("code", "a"), new("code", "c"), Subject(WestOrchidW1))];

        IReadOnlyList<ShardSearchRequest> rebuilt = Rebuilt(plan, frozen);

        IsExactly(rebuilt, frozen).ShouldBeFalse();
        ShouldBeExactly(rebuilt, original);
    }

    [Fact]
    public void ConsistentlyChangedCanonicalSetOnEveryStream_IsRebuiltAsIs()
    {
        // Replay proves only that the set is canonical and consistent; that it is the issued set is the authenticator's job.
        ShardRegistry registry = Registry();
        ShardSearchRequest[] frozen =
        [
            Post(East, Subject("Patient%2Forchid")),
            Post(West, Subject("https%3A%2F%2Feast.example%2Ffhir%2FPatient%2Forchid%2Chttps%3A%2F%2Feast-alias.example%2Ffhir%5C%2Cv%5C%242%2FPatient%2Forchid")),
        ];

        ShouldBeExactly(Rebuilt(Plan(registry, Chain), frozen), frozen);
    }

    [Fact]
    public void CertifiedFormsThatDrifted_StillClassify_AndFailExactEquality()
    {
        ShardRegistry registry = Registry();
        ShardSearchRequest[] frozen = [Post(East, Subject(EastOrchidW1)), Post(West, Subject(WestOrchidW1))];
        QueryPlan drifted = Plan(registry, ReferenceSearchForms.Relative | ReferenceSearchForms.CanonicalAbsolute, Chain);

        IReadOnlyList<ShardSearchRequest> rebuilt = Rebuilt(drifted, frozen);

        IsExactly(rebuilt, frozen).ShouldBeFalse();
        rebuilt[0].Parameters.ShouldBe([Subject(EastOrchidW1)]);
        // West's own key is now spelled relative, then canonical.
        rebuilt[1].Parameters.ShouldBe(
        [
            Subject(
                "https%3A%2F%2Feast.example%2Ffhir%2FPatient%2Forchid%2Chttps%3A%2F%2Feast-alias.example%2Ffhir%5C%2Cv%5C%242%2FPatient%2Forchid"
                + "%2CPatient%2Fw1%2Chttps%3A%2F%2Fwest.example%2Ffhir%2FPatient%2Fw1"),
        ]);
    }

    [Fact]
    public void CertifiedFormsThatNoLongerSpellAKey_RebuildNothing()
    {
        // West now admits only link base aliases for its own keys and has none, so w1 has no spelling on west.
        ShardRegistry registry = Registry();
        ShardSearchRequest[] frozen = [Post(East, Subject(EastOrchidW1)), Post(West, Subject(WestOrchidW1))];

        ShouldNotRebuild(Plan(registry, ReferenceSearchForms.LinkBaseAliases, Chain), frozen);
    }

    [Fact]
    public void FitPredicate_SeesEachActualPrimary_WithTheClampedCountAndChosenMethod()
    {
        ShardRegistry registry = MixedRegistry();
        ShardSearchRequest[] frozen =
        [
            Get(East, Subject("Patient%2Forchid")),
            Post(West, Subject("https%3A%2F%2Feast.example%2Ffhir%2FPatient%2Forchid")),
        ];
        var seen = new List<ShardSearchRequest>();

        IReadOnlyList<ShardSearchRequest> rebuilt = Rebuilt(Plan(registry, Chain), frozen, fits: r =>
        {
            seen.Add(r);
            return true;
        });

        ShouldBeExactly(rebuilt, frozen); // Templates keep the federation page size.
        ShouldBeExactly(
            seen,
            [
                new(East, Observation, [Subject("Patient%2Forchid")], 20, ShardSearchMethod.Get),
                new(West, Observation, [Subject("https%3A%2F%2Feast.example%2Ffhir%2FPatient%2Forchid")], 10, ShardSearchMethod.PostSearch),
            ]);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    public void FitPredicate_AcceptsTheExactCeiling_AndRefusesOneByteUnder(int slack, bool rebuilt)
    {
        ShardRegistry registry = MixedRegistry();
        ShardSearchRequest[] frozen =
        [
            Get(East, Subject("Patient%2Forchid")),
            Post(West, Subject("https%3A%2F%2Feast.example%2Ffhir%2FPatient%2Forchid")),
        ];
        int ceiling = Encoding.UTF8.GetByteCount("subject=https%3A%2F%2Feast.example%2Ffhir%2FPatient%2Forchid&_count=10") + slack;

        bool result = ForwardChainReplay.TryRebuild(
            Plan(registry, Chain),
            frozen,
            1000,
            r => r.Shard != West || Encoding.UTF8.GetByteCount($"{QueryRewriter.Render(r.Parameters)}&_count={r.PageSize}") <= ceiling,
            out IReadOnlyList<ShardSearchRequest>? streams);

        result.ShouldBe(rebuilt);
        (streams is not null).ShouldBe(rebuilt);
    }

    [Fact]
    public void InvalidArguments_AreRejected()
    {
        ShardRegistry registry = Registry();
        QueryPlan plan = Plan(registry, Chain);
        ShardSearchRequest[] frozen = [Post(East, Subject(EastOrchidW1)), Post(West, Subject(WestOrchidW1))];

        Should.Throw<ArgumentNullException>(() => ForwardChainReplay.TryRebuild(null!, frozen, 1, _ => true, out _)).ParamName.ShouldBe("plan");
        Should.Throw<ArgumentNullException>(() => ForwardChainReplay.TryRebuild(plan, null!, 1, _ => true, out _)).ParamName.ShouldBe("frozen");
        Should.Throw<ArgumentNullException>(() => ForwardChainReplay.TryRebuild(plan, frozen, 1, null!, out _)).ParamName.ShouldBe("fitsRequest");
        Should.Throw<ArgumentOutOfRangeException>(() => ForwardChainReplay.TryRebuild(plan, frozen, 0, _ => true, out _)).ParamName.ShouldBe("maxChainKeys");
    }

    private static ParameterOccurrence Subject(string rawValue) => new("subject", rawValue);

    /// <summary>The subject pair for <paramref name="decoded"/>, percent-encoded once as the planner encodes generated pairs.</summary>
    private static ParameterOccurrence Encoded(string decoded) => QueryRewriter.Encode("subject", decoded);

    private static ShardSearchRequest Post(ShardId shard, params ParameterOccurrence[] parameters) =>
        new(shard, Observation, parameters, 20, ShardSearchMethod.PostSearch);

    private static ShardSearchRequest Get(ShardId shard, params ParameterOccurrence[] parameters) =>
        new(shard, Observation, parameters, 20, ShardSearchMethod.Get);

    private static IReadOnlyList<ShardSearchRequest> Rebuilt(
        QueryPlan plan,
        IReadOnlyList<ShardSearchRequest> frozen,
        int maxChainKeys = 1000,
        Func<ShardSearchRequest, bool>? fits = null)
    {
        ForwardChainReplay.TryRebuild(plan, frozen, maxChainKeys, fits ?? (_ => true), out IReadOnlyList<ShardSearchRequest>? streams).ShouldBeTrue();
        return streams.ShouldNotBeNull();
    }

    private static void ShouldNotRebuild(QueryPlan plan, IReadOnlyList<ShardSearchRequest> frozen, int maxChainKeys = 1000)
    {
        ForwardChainReplay.TryRebuild(plan, frozen, maxChainKeys, _ => true, out IReadOnlyList<ShardSearchRequest>? streams).ShouldBeFalse();
        streams.ShouldBeNull();
    }

    /// <summary>The caller's comparison: shard, type, page size, method and every raw parameter, in order.</summary>
    private static bool IsExactly(IReadOnlyList<ShardSearchRequest> actual, ShardSearchRequest[] expected) =>
        actual.Count == expected.Length
        && actual.Zip(expected).All(p =>
            p.First.Shard == p.Second.Shard
            && p.First.Type == p.Second.Type
            && p.First.PageSize == p.Second.PageSize
            && p.First.Method == p.Second.Method
            && p.First.Parameters.SequenceEqual(p.Second.Parameters));

    private static void ShouldBeExactly(IReadOnlyList<ShardSearchRequest> actual, IReadOnlyList<ShardSearchRequest> expected)
    {
        actual.Count.ShouldBe(expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            (actual[i].Shard, actual[i].Type, actual[i].PageSize, actual[i].Method)
                .ShouldBe((expected[i].Shard, expected[i].Type, expected[i].PageSize, expected[i].Method));
            actual[i].Parameters.ShouldBe(expected[i].Parameters);
        }
    }
}
