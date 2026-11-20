using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Chains;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Chains.ChainScenario;

namespace Ignixa.Federation.Core.UnitTests.Execution.Chains;

/// <summary>
/// The resolved forward join, executed by the real <see cref="UnsortedPageExecutor"/> over synthetic backends (design spec
/// §7.5): which observations a chain selects in each mode, and the exact primary requests that select them.
/// </summary>
public sealed class ForwardChainJoinTests
{
    private const string OrchidChain = "subject:Patient.name=Orchid";
    private const string SubjectPassthrough = "Observation.subject:Patient";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ColocatedObservations_MatchInBothModes()
    {
        ShardRegistry registry = Registry();
        var backend = new ChainBackend(registry)
            .Patient(East, "orchid", "Orchid")
            .Observation(East, "o1", "Patient/orchid")
            .Observation(East, "o2", "Patient/orchid");

        (await JoinAsync(backend, registry, OrchidChain)).ShouldBe(["east/o1", "east/o2"]);
        (await JoinAsync(backend, registry, OrchidChain, SubjectPassthrough)).ShouldBe(["east/o1", "east/o2"]);
    }

    [Fact]
    public async Task ObservationMovedToTheOtherShardWithAnAbsoluteReference_MatchesOnlyWhenDistributed()
    {
        ShardRegistry registry = Registry();
        var backend = new ChainBackend(registry)
            .Patient(East, "orchid", "Orchid")
            .Observation(East, "o1", "Patient/orchid")
            .Observation(West, "o2", "https://east.example/fhir/Patient/orchid");

        (await JoinAsync(backend, registry, OrchidChain)).ShouldBe(["east/o1", "west/o2"]);
        (await JoinAsync(backend, registry, OrchidChain, SubjectPassthrough)).ShouldBe(["east/o1"]);
    }

    [Fact]
    public async Task EqualTargetIdOnTheWrongOrigin_IsNotSelected()
    {
        ShardRegistry registry = Registry();
        var backend = new ChainBackend(registry)
            .Patient(East, "orchid", "Orchid")
            .Patient(West, "orchid", "Basil")
            .Observation(East, "o1", "Patient/orchid")
            .Observation(West, "o2", "https://east.example/fhir/Patient/orchid")
            .Observation(West, "o3", "Patient/orchid");

        var ready = (ForwardChainReady)await Resolver(backend.Client, registry).ResolveAsync(Plan(registry, OrchidChain), Token);

        ready.Witnesses.ShouldHaveSingleItem().Key.ShouldBe(0);
        ready.Witnesses[0].ShouldBe([Key(East, Patient, "orchid")]);
        ready.Stream(West).Decoded("subject").Split(',').ShouldNotContain("Patient/orchid");
        (await ready.ExecuteAsync(backend.Client, registry, Token)).Ids().ShouldBe(["east/o1", "west/o2"]);
    }

    [Fact]
    public async Task EachShardSpellsKeysWithItsOwnCertifiedForms_AndEscapesTheAliasBeforeEncoding()
    {
        ShardRegistry registry = Registry();
        registry.Shards[0].CanonicalLinkBaseAliases.ShouldBe([EastAlias]);
        var backend = new ChainBackend(registry)
            .Patient(East, "orchid", "Orchid")
            .Patient(West, "w1", "Orchid")
            .Observation(West, "o4", "https://east-alias.example/fhir,v$2/Patient/orchid");

        var ready = (ForwardChainReady)await Resolver(backend.Client, registry).ResolveAsync(Plan(registry, OrchidChain), Token);

        // East: own keys relative, foreign keys canonical. West: own keys canonical, foreign keys canonical then aliases.
        ready.Stream(East).Parameters.ShouldBe([QueryRewriter.Encode("subject", "Patient/orchid,https://west.example/fhir/Patient/w1")]);
        string west = "https://east.example/fhir/Patient/orchid,https://east-alias.example/fhir\\,v\\$2/Patient/orchid,https://west.example/fhir/Patient/w1";
        ready.Stream(West).Parameters.ShouldBe([QueryRewriter.Encode("subject", west)]);
        ready.Stream(West).Parameters[0].Value.ShouldContain("fhir%5C%2Cv%5C%242");
        ready.Witnesses[0].ShouldBe([Key(East, Patient, "orchid"), Key(West, Patient, "w1")]);
        (await ready.ExecuteAsync(backend.Client, registry, Token)).Ids().ShouldBe(["west/o4"]);
    }

    [Fact]
    public async Task OwnerCertifiedOnlyForAliases_SpellsItsOwnKeysByEveryAlias()
    {
        ShardRegistry registry = TestShards.Create(TestShards.East(EastAlias), TestShards.West("https://w1.example/fhir", "https://w2.example/fhir"));
        var backend = new ChainBackend(registry).Patient(West, "w1", "Orchid");

        var ready = (ForwardChainReady)await Resolver(backend.Client, registry)
            .ResolveAsync(Plan(registry, ReferenceSearchForms.LinkBaseAliases, OrchidChain), Token);

        ready.Stream(West).Decoded("subject").ShouldBe("https://w1.example/fhir/Patient/w1,https://w2.example/fhir/Patient/w1");
        ready.Stream(East).Decoded("subject").ShouldBe("https://west.example/fhir/Patient/w1");
    }

    [Fact]
    public async Task KeptPredicatesStayRaw_AndEachChainIsReplacedInItsOwnPosition()
    {
        ShardRegistry registry = Registry();
        var backend = new ChainBackend(registry)
            .Patient(East, "orchid", "Orchid")
            .Practitioner(West, "doc", "Doc")
            .Observation(East, "o1", "Patient/orchid", code: "b", performer: "https://west.example/fhir/Practitioner/doc")
            .Observation(East, "o2", "Patient/orchid", code: "c", performer: "https://west.example/fhir/Practitioner/doc");
        QueryPlan plan = Plan(registry, "subject:Patient.name=Orchid&code=a%2cb&_count=7&performer:Practitioner.name=Doc");

        var ready = (ForwardChainReady)await Resolver(backend.Client, registry).ResolveAsync(plan, Token);

        ready.PageSize.ShouldBe(7);
        ready.Stream(East).Parameters.ShouldBe(
        [
            QueryRewriter.Encode("subject", "Patient/orchid"),
            new ParameterOccurrence("code", "a%2cb"),
            QueryRewriter.Encode("performer", "https://west.example/fhir/Practitioner/doc"),
        ]);
        ready.Witnesses.Keys.Order().ShouldBe([0, 3]);
        ready.Witnesses[3].ShouldBe([Key(West, Practitioner, "doc")]);
        (await ready.ExecuteAsync(backend.Client, registry, Token)).Ids().ShouldBe(["east/o1"]);
    }

    [Fact]
    public async Task WitnessSearchSendsThePlannedTerminalExactly_WithOnlyIdsRequested()
    {
        ShardRegistry registry = Registry();
        var backend = new ChainBackend(registry)
            .Patient(East, "orchid", "a,b+c")
            .Observation(East, "o1", "Patient/orchid");
        QueryPlan plan = Plan(registry, "subject:Patient.name:exact=a%5C,b%2Bc");

        var ready = (ForwardChainReady)await Resolver(backend.Client, registry).ResolveAsync(plan, Token);

        ShardSearchRequest witness = backend.Client.Calls[0].Search.ShouldNotBeNull();
        witness.Type.ShouldBe(Patient);
        witness.Parameters.ShouldBe([plan.Occurrences[0].Hop!.Terminal!, new ParameterOccurrence("_elements", "id")]);
        witness.Parameters[0].ShouldBe(QueryRewriter.Encode("name:exact", "a\\,b+c"));
        ready.Witnesses[0].ShouldBe([Key(East, Patient, "orchid")]);
    }

    [Fact]
    public async Task PassthroughChain_IsSentRawWithoutWitnessSearches()
    {
        ShardRegistry registry = Registry();
        var backend = new ChainBackend(registry);
        QueryPlan plan = Plan(registry, "subject:Patient.name=Or%63hid", SubjectPassthrough);

        var ready = (ForwardChainReady)await Resolver(backend.Client, registry).ResolveAsync(plan, Token);

        backend.Client.Calls.ShouldBeEmpty();
        ready.Witnesses.ShouldBeEmpty();
        ready.Streams.Select(s => s.Shard).ShouldBe([East, West]);
        ready.Streams.ShouldAllBe(s => s.Parameters.Single() == new ParameterOccurrence("subject:Patient.name", "Or%63hid"));
    }

    private static async Task<string[]> JoinAsync(ChainBackend backend, ShardRegistry registry, string query, params string[] passthrough)
    {
        var ready = (ForwardChainReady)await Resolver(backend.Client, registry).ResolveAsync(Plan(registry, query, passthrough), Token);
        return (await ready.ExecuteAsync(backend.Client, registry, Token)).Ids();
    }
}
