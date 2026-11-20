using Ignixa.Federation.Core.Search;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Search;

public sealed class ParameterOccurrenceTests
{
    [Theory]
    [InlineData("code", "a%2Cb")]
    [InlineData("code", "a,b")]
    [InlineData("given", "x+y")]
    [InlineData("name:exact", "%2bSarah")]
    [InlineData("family", "")]
    [InlineData(" code ", " a ")]
    public void KeepsKeyAndValueTextVerbatim(string key, string value)
    {
        var occurrence = new ParameterOccurrence(key, value);

        occurrence.Key.ShouldBe(key);
        occurrence.Value.ShouldBe(value);
    }

    [Fact]
    public void RejectsMissingKeyOrValue()
    {
        Should.Throw<ArgumentException>(() => new ParameterOccurrence(string.Empty, "x"));
        Should.Throw<ArgumentException>(() => new ParameterOccurrence(null!, "x"));
        Should.Throw<ArgumentNullException>(() => new ParameterOccurrence("code", null!));
    }
}
