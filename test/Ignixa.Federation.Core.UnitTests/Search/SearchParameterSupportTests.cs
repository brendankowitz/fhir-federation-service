using Ignixa.Federation.Core.Search;
using Ignixa.Specification.ValueSets.Normative;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Search;

public sealed class SearchParameterSupportTests
{
    // Identity is lexical, so the advertised text must survive exactly as given, and null stays "no definition".
    [Theory]
    [InlineData("https://example.invalid|1")]
    [InlineData("http://hl7.org/fhir/SearchParameter/Patient-name#v2")]
    // Accepted: the grammar is FHIR canonical `\S*`, not a URI parser. Absolute-ness is not required.
    [InlineData("urn:uuid:3f1c2b9e-0000-4000-8000-000000000001")]
    [InlineData("Patient-name")]
    [InlineData("http://x/a\u007Fb")]
    public void KeepsTheAdvertisedDefinitionLosslessly(string definition)
    {
        new SearchParameterSupport("name", SearchParamType.String, definition).Definition.ShouldBe(definition);
    }

    [Fact]
    public void AcceptsNoDefinition()
    {
        new SearchParameterSupport("name", SearchParamType.String, null).Definition.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("http://x/a b")]
    [InlineData("http://x/a\tb")]
    [InlineData("http://x/a\u0001b")]
    public void RejectsADefinitionThatIsEmptyOrHasWhitespaceOrACharacterBelowU0020(string definition)
    {
        Should.Throw<ArgumentException>(() => new SearchParameterSupport("name", SearchParamType.String, definition))
            .ParamName.ShouldBe("definition");
    }
}
