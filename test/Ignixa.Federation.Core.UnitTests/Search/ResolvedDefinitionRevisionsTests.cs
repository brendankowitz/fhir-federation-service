using System.Security.Cryptography;
using System.Text;
using Ignixa.Federation.Core.Search;
using Ignixa.Specification.ValueSets.Normative;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Search.TestSearch;

namespace Ignixa.Federation.Core.UnitTests.Search;

public sealed class ResolvedDefinitionRevisionsTests
{
    private const string OrganizationCanonical = "http://hl7.org/fhir/SearchParameter/Patient-organization";

    [Fact]
    public void ReferenceRevisionMatchesTheResolvedExpressionEvenWhenUnpublished()
    {
        SearchCapabilities view = Capabilities();
        view.Resources.Single(resource => resource.Type == Type("Patient")).Parameters
            .ShouldNotContain(parameter => parameter.Code == "organization");

        string? revision = view.GetResolvedDefinitionRevision(Type("Patient"), "organization");

        revision.ShouldBe(OrganizationRevision("Patient.managingOrganization"));
        revision.ShouldNotBe(OrganizationRevision("Patient.generalPractitioner"));
    }

    [Fact]
    public void DifferentAdvertisedCanonicalWithholdsTheRevision()
    {
        var west = new ResourceSearchSupport(Type("Patient"),
            [Param("organization", SearchParamType.Reference, OrganizationCanonical + "|2")], [], []);

        Capabilities(westResources: [west]).GetResolvedDefinitionRevision(Type("Patient"), "organization").ShouldBeNull();
    }

    private static string OrganizationRevision(string expression) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(
            "Ignixa.Federation.Search.Definition.v1\0Patient\0organization\0Reference\0",
            OrganizationCanonical, "\0", expression))));
}
