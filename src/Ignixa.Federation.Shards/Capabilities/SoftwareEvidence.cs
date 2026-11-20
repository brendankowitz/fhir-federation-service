namespace Ignixa.Federation.Shards.Capabilities;

/// <summary>
/// The <c>software.name</c> and <c>software.version</c> a shard reported, for a later check against a backend profile.
/// </summary>
/// <remarks>
/// Each part is genuinely reported or <see langword="null"/>; nothing is inferred, parsed or defaulted. A block that
/// reported only one part is not a complete identity, so a caller that needs both must check both. Only the reader creates
/// instances, and at least one part is always present.
/// </remarks>
public sealed record SoftwareEvidence
{
    internal SoftwareEvidence(string? name, string? version)
    {
        Name = name;
        Version = version;
    }

    /// <summary>Gets <c>software.name</c> exactly as reported, or <see langword="null"/> when the shard reported none.</summary>
    public string? Name { get; }

    /// <summary>Gets <c>software.version</c> exactly as reported, or <see langword="null"/> when the shard reported none.</summary>
    public string? Version { get; }
}
