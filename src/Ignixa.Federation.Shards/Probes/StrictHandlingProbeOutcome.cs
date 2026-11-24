namespace Ignixa.Federation.Shards.Probes;

/// <summary>What one strict-handling probe of a shard established, for one resource type and one wire method (design spec §6.2, §9.2).</summary>
/// <remarks>Only <see cref="Proven"/> is evidence. Every other value means the shard was not shown to reject an unadvertised parameter.</remarks>
public enum StrictHandlingProbeOutcome
{
    /// <summary>No qualifying HTTP 400 was observed for the challenge.</summary>
    ChallengeNotProven,

    /// <summary>
    /// The bounded positive search succeeded, and the same search with one unadvertised ordinary parameter was answered with
    /// HTTP 400 by the only attempt sent.
    /// </summary>
    Proven,

    /// <summary>The method is POST and the shard's profile does not offer POST search. Nothing was sent.</summary>
    MethodNotOffered,

    /// <summary>The shard's advertisement could not be fetched or read, so nothing was searched.</summary>
    AdvertisementUnavailable,

    /// <summary>The shard does not advertise <c>search-type</c> for the resource type. No search was sent.</summary>
    NotAdvertised,

    /// <summary>The positive search failed, so the shard cannot be said to reject anything. The challenge was not sent.</summary>
    ControlFailed,

    /// <summary>The challenge returned a valid search page rather than rejecting the parameter.</summary>
    ChallengeAccepted,
}
