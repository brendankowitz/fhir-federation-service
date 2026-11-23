namespace Ignixa.Federation.Api.Continuations;

/// <summary>The result of encoding or decoding the typed payload, independent of envelope redemption.</summary>
internal enum UnsortedSnapshotOutcome
{
    Success,
    Invalid,
    UnsupportedVersion,
    TooLarge,

    /// <summary>A participating shard is missing or changed; restart the search (HTTP 410).</summary>
    Gone,
}
