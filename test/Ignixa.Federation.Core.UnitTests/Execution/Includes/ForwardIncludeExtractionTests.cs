using System.Text;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Includes;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Includes.IncludeFixture;

namespace Ignixa.Federation.Core.UnitTests.Execution.Includes;

/// <summary>Which references a forward include extracts from the page's matches, and where it routes them (spec §7.6).</summary>
public sealed class ForwardIncludeExtractionTests
{
    [Fact]
    public async Task A_relative_reference_is_fetched_from_the_matchs_shard_with_one_id_search()
    {
        var client = new FakeShardClient();
        client.ServeIds(East, Pat(East, "p1"));

        IncludeResult result = await Resolver(client).ResolveAsync([Obs("o1", "Patient/p1")], ObservationSubject);

        result.Rendered().ShouldBe(["east/Patient/p1@1"]);
        result.Outcomes.ShouldBeEmpty();
        result.Fetches.ShouldBe(1);
        client.Searches().ShouldBe(["east Patient?_id=p1"]);
    }

    [Fact]
    public async Task The_page_cut_bounds_extraction_so_a_reference_in_the_lookahead_is_not_fetched()
    {
        var client = new FakeShardClient();
        ShardEntry[] observations = [Obs("o1", "Patient/p1"), Obs("o2", "Patient/p2"), Obs("o3", "Patient/p3")];
        client.OnSearch(East, Page(null, observations));
        UnsortedPage page = await Fixture.Executor(client, Registry()).ExecuteAsync(
            [new ShardSearchRequest(East, Observation, [], 20, ShardSearchMethod.Get)], 2, null, 10, TestContext.Current.CancellationToken);
        page.Matches.Count.ShouldBe(2);
        client.ServeIds(East, Pat(East, "p1"), Pat(East, "p2"), Pat(East, "p3"));

        IncludeResult result = await Resolver(client).ResolveAsync(page.Matches, ObservationSubject);

        result.Rendered().ShouldBe(["east/Patient/p1@1", "east/Patient/p2@1"]);
        client.Calls[^1].Search.ShouldNotBeNull().Type.ShouldBe(Patient);
        RequestedIds(client.Calls[^1].Search!).ShouldBe(["p1", "p2"]);
    }

    [Fact]
    public async Task A_target_repeated_on_a_later_page_is_included_again()
    {
        var client = new FakeShardClient();
        client.ServeIds(East, Pat(East, "p1"));
        ForwardIncludeResolver resolver = Resolver(client);

        IncludeResult first = await resolver.ResolveAsync([Obs("o1", "Patient/p1")], ObservationSubject);
        IncludeResult second = await resolver.ResolveAsync([Obs("o2", "Patient/p1")], ObservationSubject);

        first.Rendered().ShouldBe(["east/Patient/p1@1"]);
        second.Rendered().ShouldBe(["east/Patient/p1@1"]);
        client.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_same_relative_id_from_two_origins_is_two_targets_fetched_in_discovery_order()
    {
        var client = new FakeShardClient();
        client.ServeIds(East, Pat(East, "p1"));
        client.ServeIds(West, Pat(West, "p1"));

        IncludeResult result = await Resolver(client).ResolveAsync(
            [Obs(West, "w1", "Patient/p1"), Obs(East, "e1", "Patient/p1")],
            ObservationSubject);

        client.Searches().ShouldBe(["west Patient?_id=p1", "east Patient?_id=p1"]);
        result.Rendered().ShouldBe(["west/Patient/p1@1", "east/Patient/p1@1"]);
    }

    [Fact]
    public async Task Known_absolute_and_link_base_alias_references_route_to_the_owning_shard()
    {
        var client = new FakeShardClient();
        client.ServeIds(West, Pat(West, "p2"), Pat(West, "p3"));

        IncludeResult result = await Resolver(client, Registry("https://alias.example/fhir")).ResolveAsync(
            [Obs("o1", "https://west.example/fhir/Patient/p2"), Obs("o2", "https://alias.example/fhir/Patient/p3")],
            ObservationSubject);

        client.Searches().ShouldBe(["west Patient?_id=p2,p3"]);
        result.Rendered().ShouldBe(["west/Patient/p2@1", "west/Patient/p3@1"]);
    }

    [Fact]
    public async Task Duplicate_references_request_each_target_once()
    {
        var client = new FakeShardClient();
        client.ServeIds(East, Pat(East, "p1"), Pat(East, "p2"));

        IncludeResult result = await Resolver(client).ResolveAsync(
            [Obs("o1", "Patient/p1"), Obs("o2", "Patient/p2"), Obs("o3", "Patient/p1"), Obs("o4", "https://east.example/fhir/Patient/p2")],
            ObservationSubject);

        client.Searches().ShouldBe(["east Patient?_id=p1,p2"]);
        result.Rendered().ShouldBe(["east/Patient/p1@1", "east/Patient/p2@1"]);
    }

    [Fact]
    public async Task Groups_follow_discovery_order_and_entries_keep_backend_order()
    {
        var client = new FakeShardClient();
        client.ServeIds(East, Resource(East, Practitioner, "dr", "1"), Pat(East, "p2"), Pat(East, "p1"));

        IncludeResult result = await Resolver(client).ResolveAsync(
            [Obs(East, "o1", "Patient/p1", "Practitioner/dr"), Obs(East, "o2", "Patient/p2")],
            ObservationPerformer,
            ObservationSubject);

        client.Searches().ShouldBe(["east Practitioner?_id=dr", "east Patient?_id=p1,p2"]);
        result.Rendered().ShouldBe(["east/Practitioner/dr@1", "east/Patient/p2@1", "east/Patient/p1@1"]);
    }

    [Fact]
    public async Task A_resolve_is_filter_keeps_patient_references_and_drops_others()
    {
        ObservationPatient.Hop!.Expression.ShouldContain("resolve() is Patient");
        var client = new FakeShardClient();
        client.ServeIds(East, Pat(East, "p1"));
        client.ServeIds(West, Pat(West, "p2"), Pat(West, "p3"));

        IncludeResult result = await Resolver(client, Registry("https://alias.example/fhir")).ResolveAsync(
            [
                Obs("o1", "Patient/p1"),
                Obs("o2", "https://west.example/fhir/Patient/p2"),
                Obs("o3", "https://alias.example/fhir/Patient/p3"),
                Obs("o4", "Group/g1"),
            ],
            ObservationPatient);

        client.Searches().ShouldBe(["east Patient?_id=p1", "west Patient?_id=p2,p3"]);
        result.Rendered().ShouldBe(["east/Patient/p1@1", "west/Patient/p2@1", "west/Patient/p3@1"]);
        result.Outcomes.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_typed_include_skips_references_to_other_types()
    {
        var client = new FakeShardClient();
        client.ServeIds(East, Resource(East, Practitioner, "dr", "1"));

        IncludeResult result = await Resolver(client).ResolveAsync(
            [Obs(East, "o1", "Group/g1", "Organization/org", "Practitioner/dr")],
            ObservationSubject,
            ObservationPerformer);

        client.Searches().ShouldBe(["east Practitioner?_id=dr"]);
        result.Rendered().ShouldBe(["east/Practitioner/dr@1"]);
    }

    [Theory]
    [InlineData("\"subject\":{\"reference\":\"https://foreign.example/fhir/Patient/p1\"}")]
    [InlineData("\"subject\":{\"reference\":\"#p1\"},\"contained\":[{\"resourceType\":\"Patient\",\"id\":\"p1\"}]")]
    [InlineData("\"subject\":{\"identifier\":{\"system\":\"urn:mrn\",\"value\":\"123\"}}")]
    [InlineData("\"subject\":{\"display\":\"Someone\"}")]
    [InlineData("\"subject\":{\"reference\":\"Patient/not a valid id\"}")]
    [InlineData("\"code\":{\"text\":\"no subject\"}")]
    public async Task A_reference_that_does_not_route_to_a_registered_shard_is_not_fetched(string members)
    {
        ShardEntry match = Resource(East, Observation, "o1", "1", members);
        var client = new FakeShardClient();

        IncludeResult result = await Resolver(client).ResolveAsync([match], ObservationSubject);

        result.Includes.ShouldBeEmpty();
        result.Outcomes.ShouldBeEmpty();
        result.Fetches.ShouldBe(0);
        client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_matches_of_the_includes_source_type_are_read()
    {
        var client = new FakeShardClient();
        client.ServeIds(East, Resource(East, Organization, "org", "1"));

        IncludeResult result = await Resolver(client).ResolveAsync(
            [
                Raw(East, Observation, "broken", Encoding.UTF8.GetBytes("not json")),
                Resource(East, Patient, "p1", "1", "\"managingOrganization\":{\"reference\":\"Organization/org\"}"),
            ],
            PatientOrganization);

        result.Rendered().ShouldBe(["east/Organization/org@1"]);
    }

    [Fact]
    public async Task A_versioned_reference_fetches_the_current_version_and_reports_once_per_path()
    {
        var client = new FakeShardClient();
        client.ServeIds(East, Pat(East, "p1", "7"), Pat(East, "p2", "4"));

        IncludeResult result = await Resolver(client).ResolveAsync(
            [Obs("o1", "Patient/p1/_history/2"), Obs("o2", "Patient/p2/_history/3"), Obs("o3", "Patient/p1")],
            ObservationSubject);

        client.Searches().ShouldBe(["east Patient?_id=p1,p2"]);
        result.Rendered().ShouldBe(["east/Patient/p1@7", "east/Patient/p2@4"]);
        IncludeOutcome outcome = result.Outcomes.ShouldHaveSingleItem();
        outcome.Kind.ShouldBe(IncludeOutcomeKind.VersionedReference);
        outcome.Path.ShouldBe(ObservationSubject.Hop!.Path);
    }

    [Fact]
    public async Task An_unauthorized_target_type_is_reported_without_reading_matches_or_calling_shards()
    {
        var client = new FakeShardClient();
        client.ServeIds(East, Resource(East, Practitioner, "dr", "1"));
        var authorized = new HashSet<ResourceType> { Observation, Practitioner };

        IncludeResult result = await Resolver(client).ResolveAsync(
            [Obs(East, "o1", "Patient/p1", "Practitioner/dr")],
            [ObservationSubject, ObservationSubject, ObservationPerformer],
            authorized,
            100,
            TestContext.Current.CancellationToken);

        IncludeOutcome outcome = result.Outcomes.ShouldHaveSingleItem();
        outcome.Kind.ShouldBe(IncludeOutcomeKind.UnauthorizedType);
        outcome.Path.ShouldBe(ObservationSubject.Hop!.Path);
        client.Searches().ShouldBe(["east Practitioner?_id=dr"]);
        result.Rendered().ShouldBe(["east/Practitioner/dr@1"]);
    }

    [Fact]
    public async Task An_unauthorized_include_with_no_authorized_work_makes_no_calls_even_with_a_malformed_match()
    {
        var client = new FakeShardClient();

        IncludeResult result = await Resolver(client).ResolveAsync(
            [Raw(East, Observation, "broken", Encoding.UTF8.GetBytes("{"))],
            [ObservationSubject],
            new HashSet<ResourceType> { Observation },
            0,
            TestContext.Current.CancellationToken);

        result.Includes.ShouldBeEmpty();
        result.Outcomes.Select(o => (o.Kind, o.Path)).ShouldBe([(IncludeOutcomeKind.UnauthorizedType, ObservationSubject.Hop!.Path)]);
        result.Fetches.ShouldBe(0);
        client.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"resourceType\":\"Observation\"")]
    [InlineData("{}")]
    [InlineData("{\"resourceType\":\"Patient\",\"id\":\"o1\"}")]
    [InlineData("{\"resourceType\":5,\"id\":\"o1\"}")]
    [InlineData("null")]
    [InlineData("{\"resourceType\":\"Observation\",\"id\":\"o1\",\"subject\":{\"reference\":\"Patient/p1\"},\"subject\":{\"reference\":\"Patient/p2\"}}")]
    public async Task A_match_that_is_not_its_keys_resource_is_a_contract_violation_of_its_shard(string json)
    {
        var client = new FakeShardClient();

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(() => Resolver(client).ResolveAsync(
            [Obs(East, "ok", "Patient/p1"), Raw(West, Observation, "o1", Encoding.UTF8.GetBytes(json))],
            ObservationSubject));

        failure.Shard.ShouldBe(West);
        failure.Reason.ShouldBe(ShardFailureReason.ContractViolation);
        client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_match_with_invalid_utf8_is_a_contract_violation_of_its_shard()
    {
        var client = new FakeShardClient();
        byte[] json = [.. "{\"resourceType\":\"Observation\",\"id\":\"o1\",\"subject\":{\"reference\":\"Patient/p"u8, 0xFF, .. "\"}}"u8];

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(() => Resolver(client).ResolveAsync(
            [Raw(East, Observation, "o1", json)],
            ObservationSubject));

        failure.Shard.ShouldBe(East);
        failure.Reason.ShouldBe(ShardFailureReason.ContractViolation);
        client.Calls.ShouldBeEmpty();
    }
}
