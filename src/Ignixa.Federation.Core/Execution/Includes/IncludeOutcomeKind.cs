namespace Ignixa.Federation.Core.Execution.Includes;

/// <summary>Why a page's forward includes are incomplete or were not attempted (design spec §7.6, §10.1).</summary>
public enum IncludeOutcomeKind
{
    /// <summary>
    /// The page's included resources exceeded the per-page include limit; the first resources up to the limit are kept.
    /// Page-wide, so it carries no path.
    /// </summary>
    Truncated,

    /// <summary>
    /// A reference named a specific version. The current version was fetched instead; a versioned read is never made.
    /// </summary>
    VersionedReference,

    /// <summary>The caller may not read the include's target type, so the include was not evaluated.</summary>
    UnauthorizedType,
}
