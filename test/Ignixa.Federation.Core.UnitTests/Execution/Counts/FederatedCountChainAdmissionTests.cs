using Ignixa.Federation.Core.Execution.Counts;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Counts.CountFixture;
using static Ignixa.Federation.Core.UnitTests.Search.TestSearch;

namespace Ignixa.Federation.Core.UnitTests.Execution.Counts;

/// <summary>
/// A chain's count is exact only where its hop is certified, so the plan's hop certifications must cover the executor's
/// registry, and a reference rewrite's coverage must be attested for its version (design spec §7.5).
/// </summary>
public sealed class FederatedCountChainAdmissionTests
{
    private const string DistributedForward = "distributed forward";

    private readonly FakeShardClient _client = new();

    public static TheoryData<string> Chains => [DistributedForward, "distributed reverse", "passthrough forward", "passthrough reverse"];

    [Theory]
    [MemberData(nameof(Chains))]
    public async Task ShardAddedSinceThePlanIsRefusedEvenWhenItCertifiesTheCountShape(string chain)
    {
        (QueryPlan plan, string shape, IReadOnlyDictionary<int, string> replacements) = PlanBeforeTheChange(chain);
        ShardRegistry wider = TestShards.Create(TestShards.East(), TestShards.West(), TestShards.Shard("north", "n4rt", "https://north.example/fhir"));
        SearchCapabilities capabilities = CertifiedOn(wider, shape);
        ShardCountTarget[] targets = [.. wider.Shards.Select(s => ShardCountTarget.Search(s.Id, ShardSearchMethod.Get, replacements))];

        await Should.ThrowAsync<ExactCountNotCertifiedException>(
            () => Executor(_client, capabilities).ExecuteAsync(plan, targets, 3, Token));

        _client.Calls.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Chains))]
    public async Task ShardRemovedSinceThePlanIsRefused(string chain)
    {
        (QueryPlan plan, string shape, IReadOnlyDictionary<int, string> replacements) = PlanBeforeTheChange(chain);
        ShardRegistry narrower = TestShards.Create(TestShards.East());
        SearchCapabilities capabilities = CertifiedOn(narrower, shape);
        _client.Answer(East, 2);

        await Should.ThrowAsync<ExactCountNotCertifiedException>(
            () => Executor(_client, capabilities).ExecuteAsync(plan, [ShardCountTarget.Search(East, ShardSearchMethod.Get, replacements)], 1, Token));

        _client.Calls.ShouldBeEmpty();
    }

    /// <summary>
    /// coverage attested for the registry version; the other chains still count.
    /// </summary>
    [Theory]
    [MemberData(nameof(Chains))]
    public async Task RegistryVersionChangedSinceThePlanRefusesOnlyAReferenceRewrite(string chain)
    {
        (QueryPlan plan, string shape, IReadOnlyDictionary<int, string> replacements) = PlanBeforeTheChange(chain);
        ShardRegistry moved = TestShards.Create(TestShards.East(), TestShards.Shard("west", "p9xm", "https://west2.example/fhir"));
        moved.Version.ShouldNotBe(Registry().Version);
        SearchCapabilities capabilities = CertifiedOn(moved, shape);
        ShardCountTarget[] targets = [.. moved.Shards.Select(s => ShardCountTarget.Search(s.Id, ShardSearchMethod.Get, replacements))];
        _client.Answer(East, 2);
        _client.Answer(West, 3);

        Task<int?> counting = Executor(_client, capabilities).ExecuteAsync(plan, targets, 2, Token);

        if (chain == DistributedForward)
        {
            await Should.ThrowAsync<ExactCountNotCertifiedException>(counting);
            _client.Calls.ShouldBeEmpty();
        }
        else
        {
            (await counting).ShouldBe(5);
        }
    }

    /// <summary>Plans a count of <paramref name="chain"/> on east and west under the default registry.</summary>
    private static (QueryPlan Plan, string Shape, IReadOnlyDictionary<int, string> Replacements) PlanBeforeTheChange(string chain)
    {
        (string type, string query, string shape, string[] passthrough, string? replacement) = chain switch
        {
            DistributedForward => ("Observation", "subject:Patient.name=x&_summary=count", "Observation?subject=,", Array.Empty<string>(), "Patient/a"),
            "distributed reverse" => ("Patient", "_has:Observation:subject:code=1&_summary=count", "Patient?_id=,", [], "a"),
            "passthrough forward" => ("Observation", "subject:Patient.name=x&_summary=count", "Observation?subject:Patient.name", ["Observation.subject:Patient"], null),
            _ => ("Patient", "_has:Observation:subject:code=1&_summary=count", "Patient?_has:Observation:subject:code", ["Observation.subject:Patient"], null),
        };
        SearchCapabilities capabilities = CertifiedOn(Registry(), shape);
        QueryPlan plan = PlanOk(type, query, Planner(capabilities, passthrough));
        IReadOnlyDictionary<int, string> replacements = replacement is null ? NoReplacements : new Dictionary<int, string> { [0] = replacement };
        return (plan, shape, replacements);
    }

    /// <summary>Every shard of <paramref name="registry"/> certifies <paramref name="shape"/> exact and the subject hop for its version.</summary>
    private static SearchCapabilities CertifiedOn(ShardRegistry registry, string shape)
    {
        SearchCertification certification = Certification(
            exactCountShapes: [shape],
            paths: [FullPath(ObservationSubjectPatient, registry.Version, ["count:Observation?@Observation.subject:Patient"])]);
        return new SearchCapabilities(registry, registry.Shards.Select(s => new ShardSearchCapability(s.Id, AllResources, certification)));
    }
}
