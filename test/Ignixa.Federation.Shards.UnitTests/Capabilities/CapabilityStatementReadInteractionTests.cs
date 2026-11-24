using Ignixa.Federation.Shards.Capabilities;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities;

/// <summary>
/// Whether a server-mode resource advertises an unmodified <c>read</c>. A <c>modifierExtension</c> on <c>read</c> only
/// withholds the grant, where on <c>search-type</c> it still makes the whole statement unusable.
/// </summary>
public sealed class CapabilityStatementReadInteractionTests
{
    [Theory]
    [InlineData("""[{"code":"search-type"},{"code":"read"}]""", true)]
    [InlineData("""[{"code":"read"},{"code":"search-type"}]""", true)]
    [InlineData("""[{"code":"search-type"}]""", false)]
    [InlineData("""[{"code":"search-type"},{"code":"vread"}]""", false)]
    [InlineData("""[{"code":"search-type"},{"code":"read","modifierExtension":[]}]""", true)]
    [InlineData("""[{"code":"search-type"},{"code":"read","modifierExtension":[{"url":"http://example.test/modifier","valueBoolean":true}]}]""", false)]
    public void ReadIsAdvertised_OnlyByAnUnmodifiedReadInteraction(string interactions, bool advertised)
    {
        ResourceSearchEvidence patient = ReadPatient(interactions);

        patient.Type.Value.ShouldBe("Patient", "a modified read does not disturb the searchable resource");
        patient.AdvertisesRead.ShouldBe(advertised);
    }

    [Fact]
    public void ModifierExtensionOnSearchType_StillRejectsTheStatement()
    {
        const string Interactions = """[{"code":"read"},{"code":"search-type","modifierExtension":[{"url":"http://example.test/modifier","valueBoolean":true}]}]""";

        Should.Throw<FormatException>(() => ReadPatient(Interactions));
    }

    private static ResourceSearchEvidence ReadPatient(string interactions) =>
        CapabilityJson.Read(CapabilityJson.ServerStatement(CapabilityJson.Arr($$"""{"type":"Patient","interaction":{{interactions}}}"""))).SearchableResources.Values.Single();
}
