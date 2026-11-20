using Ignixa.Federation.Core.Search;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Search;

public sealed class QueryRewriterTests
{
    private static SearchQuery ParseOk(string encoded) => SearchQuery.Parse(encoded).Query ?? throw new InvalidOperationException("Test query did not parse.");

    [Fact]
    public void BuildsTheOriginalOccurrencesUnchangedWhenNothingIsEdited()
    {
        SearchQuery query = ParseOk("code=a%2Cb&Name:Exact=%2bx&code=a%2cb");

        IReadOnlyList<ParameterOccurrence> built = new QueryRewriter(query).Build();

        built.ShouldBe(query.Occurrences.Select(o => o.Raw));
        QueryRewriter.Render(built).ShouldBe("code=a%2Cb&Name:Exact=%2bx&code=a%2cb");
    }

    // Same key on every occurrence: only the ordinal can tell which one the planner meant.
    [Fact]
    public void DropsExactlyTheOccurrenceAtTheOrdinalNotTheFirstWithThatKey()
    {
        SearchQuery query = ParseOk("code=a&code=b&code=c");

        var rewriter = new QueryRewriter(query);
        rewriter.Drop(2);

        QueryRewriter.Render(rewriter.Build()).ShouldBe("code=a&code=b");
    }

    [Fact]
    public void ReplacesExactlyOneOccurrenceInPlaceAndKeepsTheInterleaving()
    {
        SearchQuery query = ParseOk("a=1&b=2&a=3&b=4");

        var rewriter = new QueryRewriter(query);
        rewriter.Replace(2, QueryRewriter.Encode("x", "y"));

        QueryRewriter.Render(rewriter.Build()).ShouldBe("a=1&b=2&x=y&b=4");
    }

    [Fact]
    public void KeepsDropsAndReplacesTogetherByOrdinal()
    {
        SearchQuery query = ParseOk("code=a&code=b&code=c&code=d");

        var rewriter = new QueryRewriter(query);
        rewriter.Drop(0);
        rewriter.Replace(2, QueryRewriter.Encode("code", "z"));

        QueryRewriter.Render(rewriter.Build()).ShouldBe("code=b&code=z&code=d");
    }

    [Fact]
    public void BuildReflectsTheEditsMadeSoFar()
    {
        var rewriter = new QueryRewriter(ParseOk("a=1&b=2"));

        rewriter.Build().Count.ShouldBe(2);
        rewriter.Drop(1);
        rewriter.Build().Count.ShouldBe(1);
    }

    [Fact]
    public void EncodesEachDecodedComponentOnce()
    {
        ParameterOccurrence occurrence = QueryRewriter.Encode("name:exact", "a,b&c=d é+%41");

        occurrence.Key.ShouldBe("name%3Aexact");
        occurrence.Value.ShouldBe("a%2Cb%26c%3Dd%20%C3%A9%2B%2541");
    }

    [Theory]
    [InlineData("name:exact", "a,b&c=d é+%41 \\ ~-._")]
    [InlineData("a b", "")]
    [InlineData("k=#&?", "€\U0001F600")]
    public void EncodedTextParsesBackToTheSameDecodedComponents(string key, string value)
    {
        ParameterOccurrence occurrence = QueryRewriter.Encode(key, value);

        QueryOccurrence parsed = ParseOk(QueryRewriter.Render([occurrence])).Occurrences.Single();

        parsed.Key.ShouldBe(key);
        parsed.Value.ShouldBe(value);
        parsed.Raw.ShouldBe(occurrence);
    }

    [Fact]
    public void EncodeRejectsAnEmptyKeyANullAndTextThatIsNotUnicode()
    {
        Should.Throw<ArgumentException>(() => QueryRewriter.Encode(string.Empty, "x"));
        Should.Throw<ArgumentNullException>(() => QueryRewriter.Encode(null!, "x"));
        Should.Throw<ArgumentNullException>(() => QueryRewriter.Encode("k", null!));
        Should.Throw<ArgumentException>(() => QueryRewriter.Encode("k", "\uD800"));
        Should.Throw<ArgumentException>(() => QueryRewriter.Encode("\uDC00", "x"));
    }

    [Fact]
    public void RejectsEditsThatNameNoOccurrenceOrTheSameOccurrenceTwice()
    {
        var rewriter = new QueryRewriter(ParseOk("a=1&b=2"));
        ParameterOccurrence replacement = QueryRewriter.Encode("x", "y");

        Should.Throw<ArgumentOutOfRangeException>(() => rewriter.Drop(2));
        Should.Throw<ArgumentOutOfRangeException>(() => rewriter.Drop(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => rewriter.Replace(2, replacement));
        Should.Throw<ArgumentNullException>(() => rewriter.Replace(0, null!));

        rewriter.Drop(0);
        Should.Throw<ArgumentException>(() => rewriter.Drop(0));
        Should.Throw<ArgumentException>(() => rewriter.Replace(0, replacement));
    }

    [Fact]
    public void RenderJoinsKeyEqualsValuePairsWithAmpersand()
    {
        QueryRewriter.Render([]).ShouldBe(string.Empty);
        QueryRewriter.Render([new ParameterOccurrence("a", "1"), new ParameterOccurrence("b", "")]).ShouldBe("a=1&b=");
    }
}
