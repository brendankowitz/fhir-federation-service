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

    // An occurrence is one already-split pair. A raw delimiter could not survive splitting a real query, and a client
    // that writes the text as given would send extra parameters: x&_count=5 smuggles a second _count past the page size.
    [Theory]
    [InlineData("x&_count")]
    [InlineData("a=b")]
    [InlineData("co#de")]
    public void RejectsARawDelimiterInTheKey(string key)
    {
        Should.Throw<ArgumentException>(() => new ParameterOccurrence(key, "5")).ParamName.ShouldBe("Key");
    }

    [Theory]
    [InlineData("a&_count=5")]
    [InlineData("a#b")]
    public void RejectsARawDelimiterInTheValue(string value)
    {
        Should.Throw<ArgumentException>(() => new ParameterOccurrence("code", value)).ParamName.ShouldBe("Value");
    }

    // A query splits each pair on its first '=', so a raw '=' in the value is legal; encoded delimiters are ordinary text.
    [Theory]
    [InlineData("code", "b=c")]
    [InlineData("x%26y", "a%26b")]
    [InlineData("x%3Dy", "a%23b")]
    [InlineData("x%23y", "%3D")]
    public void KeepsAValueEqualsSignAndEncodedDelimitersVerbatim(string key, string value)
    {
        var occurrence = new ParameterOccurrence(key, value);

        occurrence.Key.ShouldBe(key);
        occurrence.Value.ShouldBe(value);
    }
}
