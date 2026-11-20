namespace Ignixa.Federation.Core.Search;

/// <summary>The outcome of <see cref="SearchPlanner.Plan"/>: exactly one of a plan, a continuation, or a failure.</summary>
public sealed class SearchPlanResult
{
    private SearchPlanResult(QueryPlan? plan, ContinuationPlan? continuation, SearchFailure? failure)
    {
        Plan = plan;
        Continuation = continuation;
        Failure = failure;
    }

    /// <summary>Gets the plan for a new search, or <see langword="null"/>.</summary>
    public QueryPlan? Plan { get; }

    /// <summary>Gets the plan for a continuation request, or <see langword="null"/>.</summary>
    public ContinuationPlan? Continuation { get; }

    /// <summary>Gets why the search is refused, or <see langword="null"/>.</summary>
    public SearchFailure? Failure { get; }

    internal static SearchPlanResult Success(QueryPlan plan) => new(plan, null, null);

    internal static SearchPlanResult Continue(ContinuationPlan continuation) => new(null, continuation, null);

    internal static SearchPlanResult Fail(SearchFailureCode code, int ordinal) => new(null, null, new SearchFailure(code, ordinal));
}
