using System.Diagnostics.CodeAnalysis;

namespace Ignixa.Federation.Core.Execution.Chains;

/// <summary>
/// A distributed chain's witness searches did not finish within <c>Federation:Timeouts:ChainSubquery</c>. The message names
/// only the chain's parameter ordinal, never query text, identifiers or cursors.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "The ordinal is required.")]
public sealed class ChainSubqueryTimeoutException : TimeoutException
{
    /// <summary>Initializes a new instance of the <see cref="ChainSubqueryTimeoutException"/> class.</summary>
    /// <param name="ordinal">The chain occurrence's ordinal.</param>
    /// <param name="innerException">The cancellation the deadline caused.</param>
    public ChainSubqueryTimeoutException(int ordinal, Exception? innerException)
        : base(FormattableString.Invariant($"The chain at search parameter {ordinal} did not finish within its deadline."), innerException)
    {
        Ordinal = ordinal;
    }

    /// <summary>Gets the chain occurrence's ordinal.</summary>
    public int Ordinal { get; }
}
