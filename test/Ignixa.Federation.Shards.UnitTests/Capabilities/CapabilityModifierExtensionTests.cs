using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Shards.Capabilities;
using Shouldly;
using static Ignixa.Federation.Shards.UnitTests.Capabilities.CapabilityJson;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities;

/// <summary>
/// A modifierExtension changes the meaning of the element that carries it, so it cannot be ignored where the reader
/// takes evidence from that element. Ordinary extensions, and anything the reader does not consume, stay ignored.
/// </summary>
public sealed class CapabilityModifierExtensionTests
{
    private const string Modifier = """,{MEMBER}:[{"url":"http://example.org/ext","valueBoolean":true}]""";

    private static string Member(string name) => Modifier.Replace("{MEMBER}", $"\"{name}\"", StringComparison.Ordinal);

    /// <summary>A statement with one server block, one search-type resource with one parameter, and one common parameter, each carrying <paramref name="member"/> only at <paramref name="placement"/>.</summary>
    private static string Statement(string placement, string member)
    {
        string At(string name) => name == placement ? member : "";

        return $$"""
            {"resourceType":"CapabilityStatement","status":"active","date":"2026-01-01","kind":"instance","fhirVersion":"4.0.1"{{At("statement")}},
             "software":{"name":"s"{{At("software")}}},
             "rest":[{"mode":"server"{{At("server")}},
               "resource":[{"type":"Patient"{{At("resource")}},"interaction":[{"code":"search-type"{{At("interaction")}}}],
                 "searchParam":[{"name":"name","type":"string"{{At("param")}}}]}],
               "searchParam":[{"name":"_id","type":"token"{{At("commonParam")}}}]}]}
            """;
    }

    [Theory]
    [InlineData("statement")]
    [InlineData("software")]
    [InlineData("server")]
    [InlineData("resource")]
    [InlineData("interaction")]
    [InlineData("param")]
    [InlineData("commonParam")]
    public void AModifierExtension_OnAStructureThatSuppliesEvidence_IsRefused(string placement)
    {
        Should.Throw<FormatException>(() => Read(Statement(placement, Member("modifierExtension"))));
    }

    [Theory]
    [InlineData("statement")]
    [InlineData("software")]
    [InlineData("server")]
    [InlineData("resource")]
    [InlineData("interaction")]
    [InlineData("param")]
    [InlineData("commonParam")]
    public void AnOrdinaryExtension_OnTheSameStructures_IsIgnored(string placement)
    {
        ShardCapabilityEvidence evidence = Read(Statement(placement, Member("extension")));

        evidence.SearchableResources.Keys.ShouldBe([ResourceType.Parse("Patient")]);
        evidence.SearchableResources.Values.Single().SearchParameters.Single().Code.ShouldBe("name");
        evidence.CommonSearchParameters.Single().Code.ShouldBe("_id");
        evidence.Software!.Name.ShouldBe("s");
    }

    [Fact]
    public void AModifierExtension_WhereNothingIsConsumed_IsIgnored()
    {
        string modifier = Member("modifierExtension");
        string json = $$"""
            {"resourceType":"CapabilityStatement","status":"active","date":"2026-01-01","kind":"instance","fhirVersion":"4.0.1",
             "rest":[
               {"mode":"client"{{modifier}},"resource":[{"type":"Patient"{{modifier}}}],"searchParam":[{"name":"x","type":"string"{{modifier}}}]},
               {"mode":"server",
                 "resource":[
                   {"type":"Observation"{{modifier}},"interaction":[{"code":"read"{{modifier}}}],"searchParam":[{"name":"x","type":"string"{{modifier}}}]},
                   {"type":"Patient","interaction":[{"code":"read"{{modifier}}},{"code":"search-type"}]}]}]}
            """;

        ShardCapabilityEvidence evidence = Read(json);

        evidence.SearchableResources.Keys.ShouldBe([ResourceType.Parse("Patient")]);
    }
}
