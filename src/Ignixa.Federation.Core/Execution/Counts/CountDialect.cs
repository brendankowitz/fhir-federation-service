namespace Ignixa.Federation.Core.Execution.Counts;

/// <summary>
/// How a shard's backend answers an exact count-only search. The caller states it per shard, from the shard's exercised
/// behaviour; it is never inferred from a profile name, and a failed count never retries in another dialect.
/// </summary>
public enum CountDialect
{
    /// <summary>A search with <c>_count=0</c> returns only the exact <c>Bundle.total</c>; for example, Microsoft FHIR Server.</summary>
    CountZero,

    /// <summary>
    /// A count-only search needs <c>_summary=count</c> as well as <c>_count=0</c>; it is sent after the search's own
    /// parameters. For example, stock Ignixa SQL.
    /// </summary>
    CountZeroWithSummaryCount,
}
