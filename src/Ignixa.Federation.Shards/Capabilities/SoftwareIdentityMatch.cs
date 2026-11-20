namespace Ignixa.Federation.Shards.Capabilities;

/// <summary>Outcome of comparing an advertised software identity to an explicit profile policy.</summary>
public enum SoftwareIdentityMatch
{
    /// <summary>The software block, name, or version was not reported.</summary>
    MissingIdentity,

    /// <summary>A reported field does not conform to the policy's identity syntax.</summary>
    MalformedIdentity,

    /// <summary>The advertised name is not the configured product name.</summary>
    ProductMismatch,

    /// <summary>The advertised version does not satisfy the exact version or release range.</summary>
    VersionMismatch,

    /// <summary>Both fields match. This does not certify backend behavior.</summary>
    Matched,
}
