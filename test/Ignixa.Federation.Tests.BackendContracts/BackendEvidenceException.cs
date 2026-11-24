using System.Diagnostics.CodeAnalysis;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Why the live evidence could not be produced or trusted. A mismatch between expected and
/// actual matches is not one of these: it is a contract or characterization failure reported
/// by <see cref="MatchComparison"/>.
/// </summary>
public enum BackendFailureKind
{
    /// <summary>The container engine is missing or refused a basic request.</summary>
    Prerequisite,

    /// <summary>A container, the FHIR server, or the seed data did not become ready in time.</summary>
    Startup,

    /// <summary>A search response cannot be accounted for, so it is not evidence either way.</summary>
    IncompleteSearch,

    /// <summary>The backend rejected a search outright, which is a capability answer, not a result.</summary>
    UnsupportedCapability,

    Cleanup,
}

/// <summary>
/// A failure to produce trustworthy evidence. Messages are fixed text plus counts, status codes, and
/// exception type names. They never include response bodies, endpoints, connection strings, or the
/// inner exception, because those can carry credentials.
/// </summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "Every evidence failure needs a classification and a sanitized message, never an inner exception.")]
public sealed class BackendEvidenceException : Exception
{
    public BackendEvidenceException(BackendFailureKind kind, string detail)
        : base($"[{Tag(kind)}] {detail}")
    {
        Kind = kind;
        Detail = detail;
    }

    public BackendFailureKind Kind { get; }
    internal string Detail { get; }

    private static string Tag(BackendFailureKind kind) => kind switch
    {
        BackendFailureKind.Prerequisite => "prerequisite",
        BackendFailureKind.Startup => "startup",
        BackendFailureKind.IncompleteSearch => "incomplete-search",
        BackendFailureKind.UnsupportedCapability => "unsupported-capability",
        BackendFailureKind.Cleanup => "cleanup",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
