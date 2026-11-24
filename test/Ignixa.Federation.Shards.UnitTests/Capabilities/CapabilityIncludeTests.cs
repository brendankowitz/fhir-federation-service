using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Shards.Capabilities;
using Shouldly;
using static Ignixa.Federation.Shards.UnitTests.Capabilities.CapabilityJson;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities;

/// <summary>Explicit finite include and revinclude values are kept; wildcards are dropped; anything unusable is refused.</summary>
public sealed class CapabilityIncludeTests
{
    private static string Includes(params string[] values) => $$""","searchInclude":{{Arr(values.Select(Quote).ToArray())}}""";

    private static string RevIncludes(params string[] values) => $$""","searchRevInclude":{{Arr(values.Select(Quote).ToArray())}}""";

    private static string Quote(string value) => $"\"{value}\"";

    [Fact]
    public void ExplicitValues_KeepExactSpellingDirectionAndTargetType()
    {
        ResourceSearchEvidence patient = ReadPatient(
            Includes("Patient:organization", "Patient:general-practitioner:Practitioner")
            + RevIncludes("Observation:subject", "Observation:subject:Patient", "Encounter:patient"));

        patient.Includes.Select(i => (i.Value, i.SourceType.Value, i.Parameter, i.TargetType?.Value)).ShouldBe(
        [
            ("Patient:organization", "Patient", "organization", (string?)null),
            ("Patient:general-practitioner:Practitioner", "Patient", "general-practitioner", "Practitioner"),
        ]);
        patient.RevIncludes.Select(i => (i.Value, i.SourceType.Value, i.Parameter, i.TargetType?.Value)).ShouldBe(
        [
            ("Observation:subject", "Observation", "subject", (string?)null),
            ("Observation:subject:Patient", "Observation", "subject", "Patient"),
            ("Encounter:patient", "Encounter", "patient", null),
        ]);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("Patient:*")]
    [InlineData("Observation:*")]
    [InlineData("*:*")]
    [InlineData("Observation:subject:*")]
    [InlineData("Observation:*:Patient")]
    public void Wildcards_AreDroppedAndNeverExpanded(string wildcard)
    {
        ResourceSearchEvidence patient = ReadPatient(
            Includes(wildcard, "Patient:organization") + RevIncludes("Observation:subject", wildcard));

        patient.Includes.Select(i => i.Value).ShouldBe(["Patient:organization"]);
        patient.RevIncludes.Select(i => i.Value).ShouldBe(["Observation:subject"]);
    }

    [Fact]
    public void OnlyWildcards_LeaveNoIncludeEvidence()
    {
        ResourceSearchEvidence patient = ReadPatient(Includes("*") + RevIncludes("*"));

        patient.Includes.ShouldBeEmpty();
        patient.RevIncludes.ShouldBeEmpty();
    }

    [Fact]
    public void ARepeatedIdenticalValue_IsKeptOnce_AndCaseVariantsAreDistinct()
    {
        ResourceSearchEvidence patient = ReadPatient(
            Includes("Patient:organization", "Patient:organization", "Patient:Organization")
            + RevIncludes("Observation:subject", "Observation:subject"));

        patient.Includes.Select(i => i.Value).ShouldBe(["Patient:organization", "Patient:Organization"]);
        patient.RevIncludes.Select(i => i.Value).ShouldBe(["Observation:subject"]);
    }

    [Fact]
    public void ADeclarationOnOneDirection_IsNotEvidenceForTheOther()
    {
        ResourceSearchEvidence patient = ReadPatient(Includes("Patient:organization"));

        patient.Includes.Count.ShouldBe(1);
        patient.RevIncludes.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Observation")]
    [InlineData("Observation:")]
    [InlineData(":subject")]
    [InlineData("Observation:subject:")]
    [InlineData("Observation:subject:Patient:extra")]
    [InlineData("observation:subject")]
    [InlineData("Observation:subject:patient")]
    [InlineData("Observation:sub ject")]
    [InlineData("Observation:sub*ject")]
    [InlineData("Observation:subject ")]
    [InlineData("Observation:sub\\tject")]
    [InlineData("Observation:sub=ject")]
    [InlineData("Observation:sub&ject")]
    [InlineData("Observation:sub.ject:Patient")]
    public void AnUnusableValue_IsRefusedAndAddsNoEvidence(string value)
    {
        Should.Throw<FormatException>(() => ReadPatient(Includes(value)));
        Should.Throw<FormatException>(() => ReadPatient(RevIncludes(value)));
    }

    [Theory]
    [InlineData("""5""")]
    [InlineData("""{"a":"Patient:x"}""")]
    [InlineData("""null""")]
    [InlineData(""""Patient:organization"""")]
    public void AnIncludeMemberThatIsNotAnArray_IsRefused(string value)
    {
        Should.Throw<FormatException>(() => ReadPatient($$""","searchInclude":{{value}}"""));
        Should.Throw<FormatException>(() => ReadPatient($$""","searchRevInclude":{{value}}"""));
    }

    [Theory]
    [InlineData("5")]
    [InlineData("null")]
    [InlineData("""["Patient:organization"]""")]
    [InlineData("""{"value":"Patient:organization"}""")]
    public void AnIncludeElementThatIsNotAString_IsRefused(string element)
    {
        Should.Throw<FormatException>(() => ReadPatient($$""","searchInclude":["Patient:organization",{{element}}]"""));
        Should.Throw<FormatException>(() => ReadPatient($$""","searchRevInclude":[{{element}}]"""));
    }

    [Fact]
    public void IncludesOnAResourceThatDoesNotAdvertiseSearch_AreNotEvidence()
    {
        string resources = Arr(
            """{"type":"Observation","interaction":[{"code":"read"}],"searchInclude":["Observation:subject"]}""",
            SearchableResource("Patient"));

        Read(ServerStatement(resources)).SearchableResources.Keys.ShouldBe([ResourceType.Parse("Patient")]);
    }

    [Fact]
    public void IncludesOfOneResource_DoNotLeakToAnother()
    {
        string resources = Arr(
            SearchableResource("Patient", Includes("Patient:organization")),
            SearchableResource("Observation", RevIncludes("Provenance:target")));

        ShardCapabilityEvidence evidence = Read(ServerStatement(resources));

        evidence.SearchableResources[ResourceType.Parse("Patient")].RevIncludes.ShouldBeEmpty();
        evidence.SearchableResources[ResourceType.Parse("Observation")].Includes.ShouldBeEmpty();
    }
}
