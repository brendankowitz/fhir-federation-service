using Ignixa.Federation.Core.Identity;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Identity;

public sealed class ResourceIdTests
{
    [Theory]
    [InlineData("A1")]
    [InlineData("a")]
    [InlineData("a.b-C9")]
    [InlineData(".")]
    [InlineData("..")]
    public void Parse_AcceptsFhirIds(string value)
    {
        ResourceId.Parse(value).Value.ShouldBe(value);
        ResourceId.TryParse(value, out ResourceId? id).ShouldBeTrue();
        id!.ToString().ShouldBe(value);
    }

    [Fact]
    public void Parse_AcceptsSixtyFourCharacters()
    {
        string value = new('x', 64);

        ResourceId.Parse(value).Value.ShouldBe(value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("a_b")]
    [InlineData("a/b")]
    [InlineData("caf\u00e9")]
    [InlineData("A1\n")]
    [InlineData("\nA1")]
    [InlineData(" A1")]
    public void Parse_RejectsValuesOutsideTheFhirIdGrammar(string value)
    {
        Should.Throw<ArgumentException>(() => ResourceId.Parse(value));
        ResourceId.TryParse(value, out ResourceId? id).ShouldBeFalse();
        id.ShouldBeNull();
    }

    [Fact]
    public void Parse_RejectsSixtyFiveCharacters()
    {
        Should.Throw<ArgumentException>(() => ResourceId.Parse(new string('x', 65)));
    }

    [Fact]
    public void TryParse_RejectsNull()
    {
        ResourceId.TryParse(null, out ResourceId? id).ShouldBeFalse();
        id.ShouldBeNull();
    }

    [Fact]
    public void Equality_IsOrdinalAndCaseSensitive()
    {
        ResourceId.Parse("A1").ShouldBe(ResourceId.Parse("A1"));
        ResourceId.Parse("A1").GetHashCode().ShouldBe(ResourceId.Parse("A1").GetHashCode());
        ResourceId.Parse("A1").ShouldNotBe(ResourceId.Parse("a1"));
    }
}
