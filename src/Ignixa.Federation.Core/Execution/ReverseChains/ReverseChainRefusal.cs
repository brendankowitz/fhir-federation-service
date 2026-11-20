namespace Ignixa.Federation.Core.Execution.ReverseChains;

/// <summary>Why a reverse chain cannot be executed.</summary>
public enum ReverseChainRefusal
{
    /// <summary>
    /// The source rows or distinct physical keys exceed the configured limit, or a generated primary or count request
    /// does not fit an owning shard in its selected method.
    /// </summary>
    TooCostly,

    /// <summary>The current shards do not carry the planned path proof.</summary>
    NotCertified,

    /// <summary>The caller cannot read a type on the path.</summary>
    UnauthorizedType,
}
