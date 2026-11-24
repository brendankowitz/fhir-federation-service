using System.Text;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Shards.Capabilities;
using Shouldly;
using static Ignixa.Federation.Shards.UnitTests.Capabilities.CapabilityJson;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities;

/// <summary>Which statements are R4 advertisements at all, and which interactions count as advertising search-type.</summary>
public sealed class CapabilityStatementAdvertisementTests
{
    private static readonly ResourceType Patient = ResourceType.Parse("Patient");

    [Theory]
    [InlineData("4.0.0")]
    [InlineData("4.0.1")]
    public void R4Versions_AreRead(string version)
    {
        ShardCapabilityEvidence evidence = Read(Statement(
            Arr($$"""{"mode":"server","resource":[{{SearchableResource("Patient")}}]}"""), fhirVersion: version));

        evidence.SearchableResources.Keys.ShouldBe([Patient]);
    }

    [Theory]
    [InlineData("4.3.0")]
    [InlineData("5.0.0")]
    [InlineData("3.0.2")]
    [InlineData("4.0")]
    [InlineData("4.0.1 ")]
    [InlineData("")]
    public void OtherVersions_AreRefused(string version)
    {
        string json = Statement(
            Arr($$"""{"mode":"server","resource":[{{SearchableResource("Patient")}}]}"""), fhirVersion: version);

        Should.Throw<FormatException>(() => Read(json));
    }

    [Fact]
    public void MissingOrNonStringVersion_IsRefused()
    {
        Should.Throw<FormatException>(() => Read(ServerStatement().Replace(",\"fhirVersion\":\"4.0.1\"", string.Empty, StringComparison.Ordinal)));
        Should.Throw<FormatException>(() => Read(ServerStatement().Replace("\"fhirVersion\":\"4.0.1\"", "\"fhirVersion\":4.0", StringComparison.Ordinal)));
    }

    [Fact]
    public void ANonCapabilityStatementResource_IsRefused()
    {
        Should.Throw<FormatException>(() => Read(ServerStatement().Replace("CapabilityStatement", "Bundle", StringComparison.Ordinal)));
    }

    [Fact]
    public void AStatementOfRequirements_IsNotAnAdvertisement()
    {
        string requirements = Statement(
            Arr($$"""{"mode":"server","resource":[{{SearchableResource("Patient")}}]}"""), kind: "requirements");

        Should.Throw<FormatException>(() => Read(requirements));
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("instance")]
    public void InstanceAndCapabilityKinds_AreAdvertisements(string kind)
    {
        Read(Statement(Arr($$"""{"mode":"server","resource":[{{SearchableResource("Patient")}}]}"""), kind: kind))
            .SearchableResources.Keys.ShouldBe([Patient]);
    }

    [Fact]
    public void AStatementWithoutRest_AdvertisesNothing()
    {
        ShardCapabilityEvidence evidence = Read(Statement());

        evidence.SearchableResources.ShouldBeEmpty();
        evidence.CommonSearchParameters.ShouldBeEmpty();
        evidence.Software.ShouldBeNull();
    }

    [Fact]
    public void ClientModeOnly_AdvertisesNothing()
    {
        string client = Statement(Arr($$"""{"mode":"client","resource":[{{SearchableResource("Patient")}}],"searchParam":[{{Param("name", "string")}}]}"""));

        ShardCapabilityEvidence evidence = Read(client);

        evidence.SearchableResources.ShouldBeEmpty();
        evidence.CommonSearchParameters.ShouldBeEmpty();
    }

    [Fact]
    public void MixedServerAndClientBlocks_UseOnlyTheServerBlock()
    {
        string json = Statement(Arr(
            $$"""{"mode":"client","resource":[{{SearchableResource("Observation")}}],"searchParam":[{{Param("client-only", "token")}}]}""",
            $$"""{"mode":"server","resource":[{{SearchableResource("Patient")}}],"searchParam":[{{Param("_id", "token")}}]}"""));

        ShardCapabilityEvidence evidence = Read(json);

        evidence.SearchableResources.Keys.ShouldBe([Patient]);
        evidence.CommonSearchParameters.Select(p => p.Code).ShouldBe(["_id"]);
    }

    [Fact]
    public void OnlyTheSearchTypeInteraction_AdvertisesSearch()
    {
        string resources = Arr(
            """{"type":"Patient","interaction":[{"code":"read"},{"code":"vread"},{"code":"history-type"},{"code":"search"},{"code":"search-system"},{"code":"Search-Type"},{"code":"search-type "}]}""",
            """{"type":"Observation","interaction":[{"code":"read"},{"code":"search-type"}]}""",
            """{"type":"Encounter"}""");

        ShardCapabilityEvidence evidence = Read(ServerStatement(resources));

        evidence.SearchableResources.Keys.ShouldBe([ResourceType.Parse("Observation")]);
    }

    [Fact]
    public void SystemLevelSearch_IsNotResourceSearchEvidence()
    {
        string json = Statement("""[{"mode":"server","interaction":[{"code":"search-system"},{"code":"search-type"}],"resource":[{"type":"Patient","interaction":[{"code":"read"}]}]}]""");

        Read(json).SearchableResources.ShouldBeEmpty();
    }

    [Fact]
    public void AServerWithoutSearchTypeEvidence_IsAValidEmptyAdvertisement()
    {
        Read(ServerStatement()).SearchableResources.ShouldBeEmpty();
    }

    [Fact]
    public void ARepeatedSearchTypeInteraction_IsOneAdvertisement()
    {
        string resources = Arr("""{"type":"Patient","interaction":[{"code":"search-type"},{"code":"search-type"}]}""");

        Read(ServerStatement(resources)).SearchableResources.Keys.ShouldBe([Patient]);
    }

    [Fact]
    public void ARepeatedResourceDeclaration_IsRefusedRatherThanMerged()
    {
        string resources = Arr(
            SearchableResource("Patient", $$""","searchParam":[{{Param("name", "string")}}]"""),
            SearchableResource("Patient", $$""","searchParam":[{{Param("birthdate", "date")}}]"""));

        Should.Throw<FormatException>(() => Read(ServerStatement(resources)));
    }

    [Fact]
    public void ARepeatedDeclaration_IsRefusedEvenWhenOneCopyDoesNotSearch()
    {
        string resources = Arr(SearchableResource("Patient"), """{"type":"Patient","interaction":[{"code":"read"}]}""");

        Should.Throw<FormatException>(() => Read(ServerStatement(resources)));
    }

    [Fact]
    public void MultipleServerBlocks_AreRefusedRatherThanUnioned()
    {
        string json = Statement(Arr(
            $$"""{"mode":"server","resource":[{{SearchableResource("Patient")}}]}""",
            $$"""{"mode":"server","resource":[{{SearchableResource("Observation")}}]}"""));

        Should.Throw<FormatException>(() => Read(json));
    }

    [Theory]
    [InlineData("""{"resource":[]}""")]
    [InlineData("""{"mode":"Server","resource":[]}""")]
    [InlineData("""{"mode":"both","resource":[]}""")]
    [InlineData("""{"mode":5,"resource":[]}""")]
    [InlineData("""[]""")]
    public void ARestBlockWithoutAKnownMode_IsRefused(string block)
    {
        Should.Throw<FormatException>(() => Read(Statement($"[{block}]")));
    }

    [Theory]
    [InlineData("""{"interaction":[{"code":"search-type"}]}""")]
    [InlineData("""{"type":"patient","interaction":[{"code":"search-type"}]}""")]
    [InlineData("""{"type":"*","interaction":[{"code":"search-type"}]}""")]
    [InlineData("""{"type":7,"interaction":[{"code":"search-type"}]}""")]
    [InlineData("""{"type":"Patient","interaction":{"code":"search-type"}}""")]
    [InlineData("""{"type":"Patient","interaction":["search-type"]}""")]
    [InlineData("""{"type":"Patient","interaction":[{"code":7}]}""")]
    [InlineData("""{"type":"Patient","interaction":[{}]}""")]
    public void AMalformedResourceDeclaration_IsRefused(string resource)
    {
        Should.Throw<FormatException>(() => Read(ServerStatement(Arr(resource))));
    }

    [Fact]
    public void ResourceMustBeAnArray()
    {
        Should.Throw<FormatException>(() => Read(Statement("""[{"mode":"server","resource":{"type":"Patient"}}]""")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"resourceType":"CapabilityStatement"}""")]
    [InlineData("""{"resourceType":"CapabilityStatement","kind":"instance","fhirVersion":"4.0.1","rest":[],"rest":[]}""")]
    public void NotAWellFormedStatement_IsRefused(string body)
    {
        Should.Throw<FormatException>(() => CapabilityStatementReader.Read(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void AnUnpairedSurrogateInAConsumedString_IsRefused()
    {
        string json = ServerStatement(Arr("""{"type":"Patient","interaction":[{"code":"\ud800"}]}"""));

        Should.Throw<FormatException>(() => Read(json));
    }

    [Fact]
    public void FailureMessages_NameTheLocationButNeverEchoTheInput()
    {
        const string Marker = "PHI-LEAK-MARKER-12345";
        string json = ServerStatement(Arr($$"""{"type":"Patient","interaction":[{"code":"search-type"}],"searchParam":[{"name":"{{Marker}}","type":"{{Marker}}"}]}"""));

        FormatException failure = Should.Throw<FormatException>(() => Read(json));

        failure.Message.ShouldNotContain(Marker);
        failure.InnerException.ShouldBeNull();
        failure.Message.Length.ShouldBeLessThan(300);
    }

    [Fact]
    public void ChangingTheInputAfterReading_DoesNotChangeTheEvidence()
    {
        byte[] body = Encoding.UTF8.GetBytes(ServerStatement(Arr(SearchableResource("Patient", $$""","searchParam":[{{Param("name", "string")}}]"""))));
        ShardCapabilityEvidence evidence = CapabilityStatementReader.Read(body);

        Array.Fill(body, (byte)'x');

        evidence.SearchableResources[Patient].SearchParameters.Single().Code.ShouldBe("name");
    }

    [Fact]
    public void EvidenceCollections_CannotBeMutatedThroughTheirInterfaces()
    {
        ShardCapabilityEvidence evidence = Read(ServerStatement(
            Arr(SearchableResource("Patient", $$""","searchParam":[{{Param("name", "string")}}]""")), Arr(Param("_id", "token"))));

        ((ICollection<SearchParameterEvidence>)evidence.CommonSearchParameters).IsReadOnly.ShouldBeTrue();
        ((ICollection<SearchParameterEvidence>)evidence.SearchableResources[Patient].SearchParameters).IsReadOnly.ShouldBeTrue();
        ((ICollection<KeyValuePair<ResourceType, ResourceSearchEvidence>>)evidence.SearchableResources).IsReadOnly.ShouldBeTrue();
    }
}
