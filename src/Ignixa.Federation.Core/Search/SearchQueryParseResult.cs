namespace Ignixa.Federation.Core.Search;

/// <summary>The outcome of <see cref="SearchQuery.Parse"/>: exactly one of a query or a failure.</summary>
public sealed class SearchQueryParseResult
{
    private SearchQueryParseResult(SearchQuery? query, SearchFailure? failure)
    {
        Query = query;
        Failure = failure;
    }

    /// <summary>Gets the parsed query, or <see langword="null"/> when parsing failed.</summary>
    public SearchQuery? Query { get; }

    /// <summary>Gets why parsing failed, or <see langword="null"/> when it succeeded.</summary>
    public SearchFailure? Failure { get; }

    internal static SearchQueryParseResult Success(SearchQuery query) => new(query, null);

    internal static SearchQueryParseResult Fail(SearchFailure failure) => new(null, failure);
}
