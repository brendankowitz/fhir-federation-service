using System.Diagnostics;
using Ignixa.Federation.Core.Diagnostics;

namespace Ignixa.Federation.Shards.Diagnostics;

/// <summary>
/// Starts the client-side trace span for a call from the gateway to a shard backend (design spec §14).
/// </summary>
public static class ShardActivity
{
    /// <summary>The tag carrying the alias of the shard being called.</summary>
    public const string ShardAliasTag = "federation.shard.alias";

    /// <summary>The tag carrying the pipeline stage that issued the call.</summary>
    public const string StageTag = "federation.stage";

    /// <summary>
    /// Starts a <see cref="ActivityKind.Client"/> activity named <c>shard {stage}</c> on
    /// <see cref="FederationTelemetry.ActivitySource"/> and tags it with the shard alias and stage.
    /// </summary>
    /// <param name="shardAlias">The alias of the shard being called.</param>
    /// <param name="stage">The pipeline stage issuing the call, for example <c>search</c> or <c>capabilities</c>.</param>
    /// <returns>The started activity, or <see langword="null"/> when no listener is subscribed. Dispose it when the call completes.</returns>
    /// <exception cref="ArgumentException"><paramref name="shardAlias"/> or <paramref name="stage"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="shardAlias"/> or <paramref name="stage"/> is <see langword="null"/>.</exception>
    public static Activity? StartBackendCall(string shardAlias, string stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);

        Activity? activity = FederationTelemetry.ActivitySource.StartActivity("shard " + stage, ActivityKind.Client);
        activity?.SetTag(ShardAliasTag, shardAlias);
        activity?.SetTag(StageTag, stage);
        return activity;
    }
}
