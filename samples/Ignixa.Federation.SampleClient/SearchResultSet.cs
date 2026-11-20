namespace Ignixa.Federation.SampleClient;

/// <summary>Collects pages into one result. Entries are keyed by <c>fullUrl</c>, never by type and id.</summary>
public sealed class SearchResultSet
{
    private readonly List<FederatedEntry> _matches = [];
    private readonly List<FederatedEntry> _includes = [];
    private readonly List<OperationIssue> _outcomes = [];
    private readonly HashSet<Uri> _seenMatches = [];
    private readonly HashSet<Uri> _seenIncludes = [];

    public IReadOnlyList<FederatedEntry> Matches => _matches;

    public IReadOnlyList<FederatedEntry> Includes => _includes;

    public IReadOnlyList<OperationIssue> Outcomes => _outcomes;

    public static async Task<SearchResultSet> FromAsync(IAsyncEnumerable<SearchPage> pages, CancellationToken cancellationToken = default)
    {
        var results = new SearchResultSet();
        await foreach (var page in pages.WithCancellation(cancellationToken))
        {
            results.Add(page);
        }

        return results;
    }

    /// <summary>Adds a page. An include may legitimately repeat on a later page, so repeats are dropped.</summary>
    public void Add(SearchPage page)
    {
        _matches.AddRange(page.Matches.Where(e => _seenMatches.Add(e.FullUrl)));
        _includes.AddRange(page.Includes.Where(e => _seenIncludes.Add(e.FullUrl)));
        _outcomes.AddRange(page.Outcomes);
    }
}
