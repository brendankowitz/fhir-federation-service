namespace Ignixa.Federation.Core.Search;

/// <summary>A continuation request: a sole <c>ct</c> occurrence whose token is passed on untouched.</summary>
public sealed class ContinuationPlan
{
    internal ContinuationPlan(QueryOccurrence occurrence) => Occurrence = occurrence;

    /// <summary>Gets the <c>ct</c> occurrence.</summary>
    public QueryOccurrence Occurrence { get; }

    /// <summary>Gets the token exactly as it arrived on the wire: not decoded, not authenticated.</summary>
    public string Token => Occurrence.Raw.Value;
}
