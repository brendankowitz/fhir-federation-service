namespace Ignixa.Federation.Core.Execution.Chains;

/// <summary>
/// Why <see cref="ForwardChainResolver"/> refuses a plan instead of resolving it (design spec §7.5). The first five reasons,
/// and <see cref="Unauthorized"/>, are decided before any request is sent; the key and payload reasons while a chain's
/// witness searches drain; <see cref="RequestTooLarge"/> before the request it measures is sent.
/// </summary>
public enum ForwardChainRefusalReason
{
    /// <summary>The caller may not read the searched type or a type a chain searches, in either chain mode.</summary>
    Unauthorized,

    /// <summary>A distributed reverse chain (<c>_has</c>), which this resolver does not execute. A passthrough one is sent natively.</summary>
    ReverseChain,

    /// <summary>An <c>_include</c>, which needs include extraction this resolver does not perform.</summary>
    Include,

    /// <summary>A <c>_revinclude</c>, which needs a reverse include search this resolver does not perform.</summary>
    RevInclude,

    /// <summary>A count-only search (<c>_summary=count</c>), which needs exact federated counting.</summary>
    CountOnly,

    /// <summary>A requested total (<c>_total</c>), which needs exact federated counting.</summary>
    Total,

    /// <summary>A <c>_sort</c>, which the unsorted executor the resolved streams feed cannot honour.</summary>
    Sort,

    /// <summary>A chain's witness searches found more distinct keys than <c>Federation:Limits:MaxChainKeys</c>.</summary>
    TooManyKeys,

    /// <summary>The witness payloads received exceed the request's remaining payload budget.</summary>
    PayloadBudgetExceeded,

    /// <summary>A witness or primary request is longer than its shard's trusted request bound.</summary>
    RequestTooLarge,
}
