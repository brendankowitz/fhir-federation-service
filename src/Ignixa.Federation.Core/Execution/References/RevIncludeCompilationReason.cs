namespace Ignixa.Federation.Core.Execution.References;

/// <summary>Why <see cref="RevIncludeQueryCompiler"/> refuses to compile a reverse include search.</summary>
public enum RevIncludeCompilationReason
{
    /// <summary>
    /// A queried shard's certification does not prove this standalone reverse include search under the current registry, or
    /// it admits no spelling for one of the keys. The search is refused rather than sent without that key.
    /// </summary>
    UnsupportedEvidence,

    /// <summary>A completed request does not fit its shard's request ceiling; nothing is truncated, split or retried.</summary>
    TooCostly,
}
