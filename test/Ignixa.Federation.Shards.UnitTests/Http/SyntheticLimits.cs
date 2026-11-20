using Ignixa.Federation.Shards.Http;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>Explicit synthetic request limits for tests; production has no default to copy.</summary>
internal static class SyntheticLimits
{
    /// <summary>Far above any request the tests not about limits make, so those tests see no effect from enforcement.</summary>
    public static ShardRequestLimits Generous { get; } = new(maxGetTargetBytes: 1 << 20, maxPostBodyBytes: 1 << 20);
}
