using System.Diagnostics.CodeAnalysis;

namespace Ignixa.Federation.Api.Continuations;

/// <summary>
/// The expected failure of an <see cref="IContinuationCache"/> operation: the backend is unreachable, or the
/// operation's outcome is unknown (it may or may not have been applied). Deliberately carries no message detail, inner
/// exception, key, or value, so it can be handled and logged without disclosing backend state or continuation content.
/// </summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "Only the payload-free form may exist; a message or inner exception would carry backend or token detail.")]
internal sealed class ContinuationCacheUnavailableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ContinuationCacheUnavailableException"/> class.</summary>
    public ContinuationCacheUnavailableException()
        : base("The continuation store is unavailable or the outcome of the operation is unknown.")
    {
    }
}
