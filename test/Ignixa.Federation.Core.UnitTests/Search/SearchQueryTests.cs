using Ignixa.Federation.Core.Search;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Search;

public sealed class SearchQueryTests
{
    private static SearchQuery ParseOk(string encoded)
    {
        SearchQueryParseResult result = SearchQuery.Parse(encoded);
        result.Failure.ShouldBeNull();
        result.Query.ShouldNotBeNull();
        return result.Query;
    }

    private static SearchFailure ParseFails(string encoded)
    {
        SearchQueryParseResult result = SearchQuery.Parse(encoded);
        result.Query.ShouldBeNull();
        result.Failure.ShouldNotBeNull();
        return result.Failure;
    }

    [Fact]
    public void KeepsEveryOccurrenceInWireOrderWithRawTextAndDecodedKeyAndValue()
    {
        // A repeated chain terminal, an escaped comma, mixed key case, '+' versus '%2B', and empty values, interleaved.
        SearchQuery query = ParseOk(
            "patient.name=Smith&code=a%5C%2Cb&Name:Exact=x&patient.name=Jones&given=a+b&given=a%2Bb&family=&Name:exact=y&given=");

        query.Occurrences.Select(o => (o.Ordinal, o.Raw.Key, o.Raw.Value, o.Key, o.Value)).ShouldBe(
        [
            (0, "patient.name", "Smith", "patient.name", "Smith"),
            (1, "code", "a%5C%2Cb", "code", "a\\,b"),
            (2, "Name:Exact", "x", "Name:Exact", "x"),
            (3, "patient.name", "Jones", "patient.name", "Jones"),
            (4, "given", "a+b", "given", "a b"),
            (5, "given", "a%2Bb", "given", "a+b"),
            (6, "family", "", "family", ""),
            (7, "Name:exact", "y", "Name:exact", "y"),
            (8, "given", "", "given", ""),
        ]);
    }

    [Theory]
    [InlineData("a=1&b=2", 2)]
    [InlineData("?a=1&b=2", 2)]
    [InlineData("?", 0)]
    [InlineData("", 0)]
    public void AcceptsAnOptionalLeadingQuestionMark(string encoded, int count)
    {
        ParseOk(encoded).Occurrences.Count.ShouldBe(count);
    }

    [Fact]
    public void StripsOnlyOneLeadingQuestionMark()
    {
        SearchQuery query = ParseOk("??a=1");

        query.Occurrences.Single().Key.ShouldBe("?a");
    }

    // The '?' prefix belongs to a GET target only. A form body that starts with one has a key that starts with one.
    [Fact]
    public void ParseFormKeepsALeadingQuestionMarkAsPartOfTheFirstKey()
    {
        SearchQuery query = SearchQuery.ParseForm("?name=Alice&b=2").Query.ShouldNotBeNull();

        query.Occurrences[0].Raw.Key.ShouldBe("?name");
        query.Occurrences[0].Key.ShouldBe("?name");
        query.Occurrences[0].Value.ShouldBe("Alice");
        query.Occurrences[1].Key.ShouldBe("b");
        QueryRewriter.Render(query.Occurrences.Select(o => o.Raw)).ShouldBe("?name=Alice&b=2");
    }

    [Fact]
    public void ParseFormDecodesAndRejectsLikeParseButNeverStripsAPrefix()
    {
        SearchQuery.ParseForm("a=1&b=x+y%2B").Query.ShouldNotBeNull().Occurrences.Select(o => o.Value).ShouldBe(["1", "x y+"]);
        SearchQuery.ParseForm(string.Empty).Query.ShouldNotBeNull().Occurrences.ShouldBeEmpty();

        SearchQuery.ParseForm("?").Failure.ShouldNotBeNull().Code.ShouldBe(SearchFailureCode.MissingEquals);
        SearchQuery.ParseForm("a=1&b=%G1").Failure.ShouldNotBeNull().Ordinal.ShouldBe(1);
        Should.Throw<ArgumentNullException>(() => SearchQuery.ParseForm(null!));
    }

    [Fact]
    public void DecodesPlusAsSpaceAndEscapedPlusAsAPlus()
    {
        SearchQuery query = ParseOk("k+k=a+b&k%2Bk=a%2Bb");

        query.Occurrences[0].Key.ShouldBe("k k");
        query.Occurrences[0].Value.ShouldBe("a b");
        query.Occurrences[1].Key.ShouldBe("k+k");
        query.Occurrences[1].Value.ShouldBe("a+b");
    }

    [Fact]
    public void DecodesEachComponentExactlyOnce()
    {
        SearchQuery query = ParseOk("a%2541=%2541%252B");

        query.Occurrences.Single().Key.ShouldBe("a%41");
        query.Occurrences.Single().Value.ShouldBe("%41%2B");
    }

    [Fact]
    public void PreservesPercentCasingAndBackslashInTheRawText()
    {
        SearchQuery query = ParseOk("code=%2c&code=%2C&code=a\\b&%7e=%7E");

        query.Occurrences.Select(o => o.Raw.Value).ShouldBe(["%2c", "%2C", "a\\b", "%7E"]);
        query.Occurrences[0].Value.ShouldBe(",");
        query.Occurrences[1].Value.ShouldBe(",");
        query.Occurrences[2].Value.ShouldBe("a\\b");
        query.Occurrences[3].Raw.Key.ShouldBe("%7e");
        query.Occurrences[3].Key.ShouldBe("~");
    }

    [Fact]
    public void SplitsOnTheFirstEqualsSignAndDecodesUtf8()
    {
        SearchQuery query = ParseOk("a=b=c&n=%C3%A9%E2%82%AC&k%3Dx=1");

        query.Occurrences[0].Key.ShouldBe("a");
        query.Occurrences[0].Value.ShouldBe("b=c");
        query.Occurrences[1].Value.ShouldBe("é€");
        query.Occurrences[2].Key.ShouldBe("k=x");
    }

    [Fact]
    public void RoundTripsThroughRender()
    {
        const string Wire = "code=a%5C%2Cb&Name:Exact=%2bx&given=a+b&family=&code=a%2Cb&k%3Dx=b=c";

        QueryRewriter.Render(ParseOk("?" + Wire).Occurrences.Select(o => o.Raw)).ShouldBe(Wire);
    }

    [Theory]
    [InlineData("a=%", 0, SearchFailureCode.MalformedPercentEncoding)]
    [InlineData("a=%4", 0, SearchFailureCode.MalformedPercentEncoding)]
    [InlineData("a=1&b=%G1", 1, SearchFailureCode.MalformedPercentEncoding)]
    [InlineData("a%zz=1", 0, SearchFailureCode.MalformedPercentEncoding)]
    [InlineData("a=%C3", 0, SearchFailureCode.InvalidUtf8)]
    [InlineData("a=%FF", 0, SearchFailureCode.InvalidUtf8)]
    [InlineData("a=%C0%AF", 0, SearchFailureCode.InvalidUtf8)]
    [InlineData("a=%ED%A0%80", 0, SearchFailureCode.InvalidUtf8)]
    [InlineData("%E2%82=1", 0, SearchFailureCode.InvalidUtf8)]
    [InlineData("a", 0, SearchFailureCode.MissingEquals)]
    [InlineData("a=1&b", 1, SearchFailureCode.MissingEquals)]
    [InlineData("=x", 0, SearchFailureCode.EmptyKey)]
    [InlineData("a=1&&b=2", 1, SearchFailureCode.EmptyPair)]
    [InlineData("a=1&", 1, SearchFailureCode.EmptyPair)]
    [InlineData("&a=1", 0, SearchFailureCode.EmptyPair)]
    [InlineData("a=1#frag", 0, SearchFailureCode.InvalidCharacter)]
    [InlineData("a=b c", 0, SearchFailureCode.InvalidCharacter)]
    [InlineData("a=é", 0, SearchFailureCode.InvalidCharacter)]
    [InlineData("a=1\r\nb=2", 0, SearchFailureCode.InvalidCharacter)]
    public void RejectsBadWireFormsWithoutSkippingThem(string encoded, int ordinal, SearchFailureCode code)
    {
        SearchFailure failure = ParseFails(encoded);

        failure.Code.ShouldBe(code);
        failure.Ordinal.ShouldBe(ordinal);
    }

    [Fact]
    public void FailureMessageDoesNotEchoTheInput()
    {
        foreach (string encoded in new[] { "ssn-123-45=%zz", "name=Jane%2", "family=Jane%FF", "Jane-Doe", "jane=1&&x=2", "jane é=1" })
        {
            SearchFailure failure = ParseFails(encoded);

            failure.Message.ShouldNotBeNullOrWhiteSpace();
            failure.Message.ShouldNotContain("jane", Case.Insensitive);
            failure.Message.ShouldNotContain("ssn", Case.Insensitive);
            failure.Message.ShouldNotContain("123", Case.Insensitive);
        }
    }

    [Fact]
    public void RejectsNull()
    {
        Should.Throw<ArgumentNullException>(() => SearchQuery.Parse(null!));
    }
}
