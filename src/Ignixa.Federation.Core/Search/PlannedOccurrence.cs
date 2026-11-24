namespace Ignixa.Federation.Core.Search;

/// <summary>One query occurrence and what the planner decided for it.</summary>
public sealed class PlannedOccurrence
{
    internal PlannedOccurrence(QueryOccurrence occurrence, OccurrenceKind kind, OccurrenceAction action, ReferenceHop? hop, IFederationSearchSpecification specification)
    {
        Occurrence = occurrence;
        Kind = kind;
        Action = action;
        Hop = hop;
        Specification = specification;
    }

    internal IFederationSearchSpecification Specification { get; }

    /// <summary>Gets the occurrence: its ordinal, original wire text and decoded pair.</summary>
    public QueryOccurrence Occurrence { get; }

    /// <summary>Gets what the occurrence is.</summary>
    public OccurrenceKind Kind { get; }

    /// <summary>Gets what backend requests do with it.</summary>
    public OccurrenceAction Action { get; }

    /// <summary>Gets the reference hop of a chain, include or reverse include; <see langword="null"/> for predicates and controls.</summary>
    public ReferenceHop? Hop { get; }

    /// <summary>Gets the decoded backend parameter key for a distributed replacement; otherwise <see langword="null"/>.</summary>
    public string? ReplacementKey => Action == OccurrenceAction.Replace
        ? Kind == OccurrenceKind.ReverseChain ? "_id" : Hop!.Path.Code
        : null;
}
