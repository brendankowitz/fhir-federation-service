using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Shards.Capabilities;
using Shouldly;
using static Ignixa.Federation.Shards.UnitTests.Capabilities.CapabilityJson;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities;

/// <summary>How reported search parameters are kept: typed, scoped, lexically exact, and refused when ambiguous or malformed.</summary>
public sealed class CapabilitySearchParameterTests
{
    private static string Params(params string[] parameters) => $$""","searchParam":{{Arr(parameters)}}""";

    [Fact]
    public void CommonAndResourceParameters_StayInTheirOwnScopes()
    {
        string json = ServerStatement(
            Arr(SearchableResource("Patient", Params(Param("name", "string"), Param("_id", "string")))),
            Arr(Param("_id", "token"), Param("_lastUpdated", "date")));

        ShardCapabilityEvidence evidence = Read(json);

        evidence.CommonSearchParameters.Select(p => (p.Code, p.Type)).ShouldBe(
            [("_id", SearchParameterType.Token), ("_lastUpdated", SearchParameterType.Date)]);
        evidence.SearchableResources[ResourceType.Parse("Patient")].SearchParameters.Select(p => (p.Code, p.Type)).ShouldBe(
            [("name", SearchParameterType.String), ("_id", SearchParameterType.String)]);
    }

    [Theory]
    [InlineData("number", SearchParameterType.Number)]
    [InlineData("date", SearchParameterType.Date)]
    [InlineData("string", SearchParameterType.String)]
    [InlineData("token", SearchParameterType.Token)]
    [InlineData("reference", SearchParameterType.Reference)]
    [InlineData("composite", SearchParameterType.Composite)]
    [InlineData("quantity", SearchParameterType.Quantity)]
    [InlineData("uri", SearchParameterType.Uri)]
    [InlineData("special", SearchParameterType.Special)]
    public void EveryR4ParameterType_IsRecognised(string wire, SearchParameterType expected)
    {
        ResourceSearchEvidence patient = ReadPatient(Params(Param("p", wire)));

        patient.SearchParameters.Single().Type.ShouldBe(expected);
    }

    [Fact]
    public void CodeCaseDefinitionAndVersion_AreKeptExactly()
    {
        const string Versioned = "http://hl7.org/fhir/SearchParameter/Patient-name|4.0.1";
        const string Mixed = "HTTP://Example.org/SP/Case";
        ResourceSearchEvidence patient = ReadPatient(Params(
            Param("Name", "string", Versioned), Param("name", "string", Mixed), Param("plain", "token")));

        patient.SearchParameters.Select(p => (p.Code, p.Type, p.Definition)).ShouldBe(
        [
            ("Name", SearchParameterType.String, (string?)Versioned),
            ("name", SearchParameterType.String, Mixed),
            ("plain", SearchParameterType.Token, null),
        ]);
    }

    [Fact]
    public void AMissingDefinition_IsNullButAMalformedOneIsRefused()
    {
        ReadPatient(Params(Param("name", "string"))).SearchParameters.Single().Definition.ShouldBeNull();

        foreach (string bad in new[] { "5", "null", "\"\"", "\"   \"", "[]", "{}", "true" })
        {
            string param = $$"""{"name":"name","type":"string","definition":{{bad}}}""";
            Should.Throw<FormatException>(() => ReadPatient(Params(param)), bad);
        }
    }

    [Theory]
    [InlineData("""{"type":"string"}""")]
    [InlineData("""{"name":"","type":"string"}""")]
    [InlineData("""{"name":"  ","type":"string"}""")]
    [InlineData("""{"name":5,"type":"string"}""")]
    [InlineData("""{"name":"x"}""")]
    [InlineData("""{"name":"x","type":"Token"}""")]
    [InlineData("""{"name":"x","type":"quantity "}""")]
    [InlineData("""{"name":"x","type":"resource"}""")]
    [InlineData("""{"name":"x","type":"unknown"}""")]
    [InlineData("""{"name":"x","type":5}""")]
    [InlineData("""{"name":"x","type":null}""")]
    [InlineData("\"name\"")]
    [InlineData("[]")]
    public void AMalformedParameter_IsRefusedNotDroppedOrDefaulted(string parameter)
    {
        Should.Throw<FormatException>(() => ReadPatient(Params(parameter)));
        Should.Throw<FormatException>(() => Read(ServerStatement(searchParam: Arr(parameter))));
    }

    [Fact]
    public void SearchParamThatIsNotAnArray_IsRefused()
    {
        Should.Throw<FormatException>(() => ReadPatient(""","searchParam":{"name":"x","type":"string"}"""));
        Should.Throw<FormatException>(() => Read(ServerStatement(searchParam: """{"name":"x","type":"string"}""")));
    }

    [Fact]
    public void AnIdenticalRepeat_IsOneParameter()
    {
        string repeated = Param("name", "string", "http://example.org/sp");

        ReadPatient(Params(repeated, repeated)).SearchParameters.Count.ShouldBe(1);
        Read(ServerStatement(searchParam: Arr(repeated, repeated))).CommonSearchParameters.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("string", "token", null, null)]
    [InlineData("string", "string", "http://example.org/a", "http://example.org/b")]
    [InlineData("string", "string", "http://example.org/a", null)]
    [InlineData("string", "string", null, "http://example.org/a")]
    [InlineData("string", "string", "http://example.org/a|1", "http://example.org/a|2")]
    public void ConflictingParametersOfOneCode_AreRefusedWhicheverComesFirst(string type1, string type2, string? definition1, string? definition2)
    {
        string first = Param("name", type1, definition1);
        string second = Param("name", type2, definition2);

        Should.Throw<FormatException>(() => ReadPatient(Params(first, second)));
        Should.Throw<FormatException>(() => ReadPatient(Params(second, first)));
        Should.Throw<FormatException>(() => Read(ServerStatement(searchParam: Arr(first, second))));
    }

    [Fact]
    public void CodesDifferingOnlyByCase_AreDistinctParameters()
    {
        ReadPatient(Params(Param("name", "string"), Param("Name", "token"))).SearchParameters.Count.ShouldBe(2);
    }

    [Fact]
    public void ParametersOfAResourceThatDoesNotAdvertiseSearch_AreNotConsumed()
    {
        string resources = Arr(
            """{"type":"Observation","interaction":[{"code":"read"}],"searchParam":[{"name":5}],"searchInclude":[5]}""",
            SearchableResource("Patient"));

        Read(ServerStatement(resources)).SearchableResources.Keys.ShouldBe([ResourceType.Parse("Patient")]);
    }

    [Fact]
    public void ARepeatedCodeInADifferentScope_IsNotAConflict()
    {
        Read(ServerStatement(
            Arr(SearchableResource("Patient", Params(Param("_id", "string")))),
            Arr(Param("_id", "token")))).CommonSearchParameters.Single().Type.ShouldBe(SearchParameterType.Token);
    }
}
