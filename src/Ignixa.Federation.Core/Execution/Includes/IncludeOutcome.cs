using Ignixa.Federation.Core.Search;

namespace Ignixa.Federation.Core.Execution.Includes;

/// <summary>A reportable fact about a page's forward includes, for the bundle's OperationOutcome (design spec §7.6).</summary>
public sealed class IncludeOutcome
{
    internal IncludeOutcome(IncludeOutcomeKind kind, ReferencePath? path)
    {
        Kind = kind;
        Path = path;
    }

    /// <summary>Gets what happened.</summary>
    public IncludeOutcomeKind Kind { get; }

    /// <summary>
    /// Gets the include the outcome is about, or <see langword="null"/> for the page-wide
    /// <see cref="IncludeOutcomeKind.Truncated"/>.
    /// </summary>
    public ReferencePath? Path { get; }
}
