namespace Ignixa.Federation.Core.Shards;

/// <summary>How a backend's <c>next</c> link is redeemed to fetch the following page (design spec §7.4, §9.2).</summary>
public enum NextLinkRedemption
{
    /// <summary>Send <c>GET</c> to the next link as given.</summary>
    FollowGet,

    /// <summary>
    /// Re-post the next link's query to <c>[base]/[type]/_search</c>, for streams started with POST whose next link is a
    /// GET URL too long to send.
    /// </summary>
    RepostToSearch,
}
