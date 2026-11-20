using Ignixa.Federation.Core.Identity;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Identity;

public sealed class ResourceTypeTests
{
    [Theory]
    [InlineData("Patient")]
    [InlineData("A")]
    [InlineData("MedicationRequest")]
    public void Parse_AcceptsTypeNameSegments(string value)
    {
        ResourceType.Parse(value).Value.ShouldBe(value);
        ResourceType.TryParse(value, out ResourceType? type).ShouldBeTrue();
        type!.ToString().ShouldBe(value);
    }

    [Fact]
    public void Parse_AcceptsSixtyFourCharactersAndRejectsSixtyFive()
    {
        ResourceType.Parse("A" + new string('b', 63)).Value.Length.ShouldBe(64);
        Should.Throw<ArgumentException>(() => ResourceType.Parse("A" + new string('b', 64)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("patient")]
    [InlineData("Patient1")]
    [InlineData("Pa-tient")]
    [InlineData("Patient\n")]
    [InlineData("P\u00e4tient")]
    public void Parse_RejectsOtherText(string value)
    {
        Should.Throw<ArgumentException>(() => ResourceType.Parse(value));
        ResourceType.TryParse(value, out ResourceType? type).ShouldBeFalse();
        type.ShouldBeNull();
    }

    [Fact]
    public void TryParse_RejectsNull()
    {
        ResourceType.TryParse(null, out _).ShouldBeFalse();
    }

    [Fact]
    public void Equality_IsOrdinal()
    {
        ResourceType.Parse("Patient").ShouldBe(ResourceType.Parse("Patient"));
        ResourceType.Parse("Patient").ShouldNotBe(ResourceType.Parse("PatienT"));
    }
}
