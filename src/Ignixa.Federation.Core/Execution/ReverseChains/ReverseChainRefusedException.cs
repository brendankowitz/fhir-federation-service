using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Search;

namespace Ignixa.Federation.Core.Execution.ReverseChains;

/// <summary>A reverse-chain path cannot be executed. The message contains no predicate values or resource ids.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "A refusal always identifies the path and reason.")]
public sealed class ReverseChainRefusedException : Exception
{
    internal ReverseChainRefusedException(ReferencePath path, ReverseChainRefusal reason)
        : base($"Reverse chain '{path}' refused: {reason}.")
    {
        Path = path;
        Reason = reason;
    }

    /// <summary>Gets the offending typed reference path.</summary>
    public ReferencePath Path { get; }

    /// <summary>Gets why the path was refused.</summary>
    public ReverseChainRefusal Reason { get; }
}
