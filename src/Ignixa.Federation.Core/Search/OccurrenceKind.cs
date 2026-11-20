namespace Ignixa.Federation.Core.Search;

/// <summary>What the planner recognised a query occurrence as.</summary>
public enum OccurrenceKind
{
    /// <summary>A search parameter on the searched type, such as <c>name=smith</c>.</summary>
    Predicate,

    /// <summary>A one-hop forward chain, such as <c>subject:Patient.name=smith</c>.</summary>
    ForwardChain,

    /// <summary>A one-hop reverse chain, such as <c>_has:Observation:subject:code=1234</c>.</summary>
    ReverseChain,

    /// <summary>An <c>_include</c>, which the federation answers per page.</summary>
    Include,

    /// <summary>A <c>_revinclude</c>, which the federation answers per page.</summary>
    RevInclude,

    /// <summary>A result control, such as <c>_count</c>, <c>_total</c> or <c>_sort</c>.</summary>
    Control,
}
