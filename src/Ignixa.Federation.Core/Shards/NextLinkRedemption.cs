namespace Ignixa.Federation.Core.Shards;

/// <summary>
/// How a backend's <c>next</c> link is redeemed to fetch the following page (design spec §7.4, §9.2). A
/// <see cref="BackendProfile"/> holds the policy; <see cref="BackendProfile.RedemptionFor"/> resolves it for a stream.
/// </summary>
public enum NextLinkRedemption
{
    /// <summary>Send <c>GET</c> to the next link as given. Every stream started with GET is redeemed this way.</summary>
    FollowGet,

    /// <summary>
    /// Re-post the next link's query to <c>[base]/[type]/_search</c>. Applies only to streams the federation started with
    /// POST, whose next link is a GET URL that may be too long to send; requires a backend that supports POST search.
    /// </summary>
    RepostToSearch,
}
