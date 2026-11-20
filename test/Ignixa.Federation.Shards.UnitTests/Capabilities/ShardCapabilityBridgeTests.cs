using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Capabilities;
using Ignixa.Specification.ValueSets.Normative;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities;

public sealed class ShardCapabilityBridgeTests
{
    private const string NameDefinition = "http://hl7.org/fhir/SearchParameter/Patient-name";
    private const string OtherDefinition = "https://example.invalid/SearchParameter/another-name";

    [Theory]
    [InlineData(false, "token", NameDefinition, SearchParamType.Token, NameDefinition)]
    [InlineData(true, "token", NameDefinition, SearchParamType.Token, NameDefinition)]
    [InlineData(false, "string", OtherDefinition, SearchParamType.String, OtherDefinition)]
    [InlineData(true, "string", OtherDefinition, SearchParamType.String, OtherDefinition)]
    public void CommonDeclarationsReachEverySearchableResourceAndNeitherSideOfAConflictDisappears(
        bool reverse, string type, string definition, SearchParamType expectedType, string expectedDefinition)
    {
        string id = CapabilityJson.Param("_id", "token");
        string commonName = CapabilityJson.Param("name", type, definition);
        string[] common = reverse ? [commonName, id] : [id, commonName];
        string resources = CapabilityJson.Arr(
            CapabilityJson.SearchableResource("Patient", ",\"searchParam\":" +
                CapabilityJson.Arr(CapabilityJson.Param("name", "string", NameDefinition))),
            CapabilityJson.SearchableResource("Organization"),
            """{"type":"Observation","interaction":[{"code":"read"}]}""");
        ShardCapabilityEvidence evidence = CapabilityJson.Read(CapabilityJson.ServerStatement(resources, CapabilityJson.Arr(common)));
        ShardRegistry registry = Registry();

        ShardSearchCapability mapped = ShardCapabilityBridge.Create(registry.Shards.Single(), evidence, Certification());

        mapped.Resources.Select(r => r.Type.Value).ShouldBe(["Organization", "Patient"], ignoreOrder: true);
        ResourceSearchSupport patient = mapped.Resources.Single(r => r.Type.Value == "Patient");
        patient.Parameters.Count(p => p.Code == "_id" && p.Type == SearchParamType.Token && p.Definition is null).ShouldBe(1);
        patient.Parameters.Where(p => p.Code == "name").Select(p => (p.Type, p.Definition?.OriginalString))
            .ShouldBe([(SearchParamType.String, NameDefinition), (expectedType, expectedDefinition)], ignoreOrder: true);
        ResourceSearchSupport organization = mapped.Resources.Single(r => r.Type.Value == "Organization");
        organization.Parameters.Select(p => (p.Code, p.Type, p.Definition?.OriginalString))
            .ShouldBe([("_id", SearchParamType.Token, null), ("name", expectedType, expectedDefinition)], ignoreOrder: true);

        var admitted = new SearchCapabilities(registry, [mapped]);
        admitted.Resources.Single(r => r.Type.Value == "Patient").Parameters.Any(p => p.Code == "name").ShouldBeFalse();
    }

    [Theory]
    [InlineData("number", SearchParamType.Number)]
    [InlineData("date", SearchParamType.Date)]
    [InlineData("string", SearchParamType.String)]
    [InlineData("token", SearchParamType.Token)]
    [InlineData("reference", SearchParamType.Reference)]
    [InlineData("composite", SearchParamType.Composite)]
    [InlineData("quantity", SearchParamType.Quantity)]
    [InlineData("uri", SearchParamType.Uri)]
    [InlineData("special", SearchParamType.Special)]
    public void CopiesTheExactWireCanonicalAndMapsEveryR4Type(string wireType, SearchParamType expectedType)
    {
        const string definition = "https://EXAMPLE.invalid:443/parameters/%61|2.1#slice";
        string parameters = CapabilityJson.Arr(CapabilityJson.Param("code-A", wireType, definition));
        ShardCapabilityEvidence evidence = ReadPatient(",\"searchParam\":" + parameters);

        ShardSearchCapability mapped = ShardCapabilityBridge.Create(Registry().Shards.Single(), evidence, Certification());

        SearchParameterSupport parameter = mapped.Resources.ShouldHaveSingleItem().Parameters.ShouldHaveSingleItem();
        parameter.Code.ShouldBe("code-A");
        parameter.Type.ShouldBe(expectedType);
        Uri actual = parameter.Definition.ShouldNotBeNull();
        actual.IsAbsoluteUri.ShouldBeTrue();
        actual.OriginalString.ShouldBe(definition);
    }

    [Fact]
    public void FiniteFormsAreKeptExactlyWithoutAddingProof()
    {
        ShardCapabilityEvidence evidence = ReadPatient("""
            ,"searchInclude":["*","Patient:*","Patient:organization","Patient:general-practitioner:Practitioner"],
            "searchRevInclude":["Observation:*","Observation:subject:Patient","Observation:subject"],
            "searchParam":[{"name":"name","type":"string"},{"name":"organization","type":"reference"}]
            """, ""","software":{"name":"FHIR Server for Azure","version":"5.2.84"}""");
        ShardRegistry registry = Registry();
        SearchCertification proof = Certification();

        ShardSearchCapability mapped = ShardCapabilityBridge.Create(registry.Shards.Single(), evidence, proof);

        mapped.Shard.Value.ShouldBe("east");
        mapped.Certification.ShouldBeSameAs(proof);
        ResourceSearchSupport patient = mapped.Resources.ShouldHaveSingleItem();
        patient.SearchIncludes.ShouldBe(["Patient:organization", "Patient:general-practitioner:Practitioner"]);
        patient.SearchRevIncludes.ShouldBe(["Observation:subject:Patient", "Observation:subject"]);
        var admitted = new SearchCapabilities(registry, [mapped]);
        admitted.Resources.Single().Parameters.Select(p => p.Code).ShouldBe(["name"]);
        admitted.Resources.Single().SearchIncludes.ShouldBeEmpty();
        admitted.Resources.Single().SearchRevIncludes.ShouldBeEmpty();
        mapped.Certification.ExactCountShapes.ShouldBeEmpty();
        mapped.Certification.ReferencePaths.ShouldBeEmpty();
    }

    [Fact]
    public void SuppliedProofIsKeptWithoutReplacingOrExtendingIt()
    {
        var modifiers = new Dictionary<SearchParamType, IReadOnlyCollection<string>> { [SearchParamType.String] = ["exact"] };
        var proof = new SearchCertification(modifiers, [], [], ["Patient?name:exact"], []);
        ShardCapabilityEvidence evidence = ReadPatient();

        ShardSearchCapability mapped = ShardCapabilityBridge.Create(Registry().Shards.Single(), evidence, proof);

        mapped.Certification.ShouldBeSameAs(proof);
        mapped.Certification.SupportsModifier(SearchParamType.String, "exact").ShouldBeTrue();
        mapped.Certification.SupportsModifier(SearchParamType.String, "contains").ShouldBeFalse();
    }

    [Fact]
    public void EmptyAdvertisementCannotProduceASearchCapability()
    {
        ShardCapabilityEvidence evidence = CapabilityJson.Read(CapabilityJson.ServerStatement());

        Should.Throw<ArgumentException>(() => ShardCapabilityBridge.Create(Registry().Shards.Single(), evidence, Certification()));
    }

    [Fact]
    public void AnAdvertisedCodeOutsideThePlannerGrammarCannotYieldAPartialCapability()
    {
        string parameters = CapabilityJson.Arr(CapabilityJson.Param("name", "string"), CapabilityJson.Param("1unsupported", "string"));
        ShardCapabilityEvidence evidence = ReadPatient(",\"searchParam\":" + parameters);

        Should.Throw<ArgumentException>(() => ShardCapabilityBridge.Create(Registry().Shards.Single(), evidence, Certification()));
    }

    private static ShardCapabilityEvidence ReadPatient(string members = "", string extraRoot = "") =>
        CapabilityJson.Read(CapabilityJson.ServerStatement(
            CapabilityJson.Arr(CapabilityJson.SearchableResource("Patient", members)),
            extraRoot: extraRoot));

    private static SearchCertification Certification() =>
        new(new Dictionary<SearchParamType, IReadOnlyCollection<string>>(), [], [], [], []);

    private static ShardRegistry Registry() => ShardRegistry.Create(
        [new ShardOptions
        {
            Id = "east",
            Alias = "east",
            BaseUrl = "https://east.example.invalid/fhir",
            Profile = "Synthetic",
            Auth = new AuthOptions { Type = "ManagedIdentity", Resource = "api://synthetic" },
        }],
        [new BackendProfile("Synthetic", SupportsPostSearch: false, MaxCount: 50, NextLinkRedemption.FollowGet, null)]);
}
