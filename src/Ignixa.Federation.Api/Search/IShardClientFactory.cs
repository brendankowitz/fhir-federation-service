using Ignixa.Federation.Api.Budgets;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Api.Search;

/// <summary>
/// Supplies the shard transport for one inbound search (design spec §7.8, §8). The host registers exactly one
/// implementation; the search route calls it once per request, after the caller is authorised and the query planned,
/// and before any shard I/O.
/// </summary>
/// <remarks>
/// <para>
/// The factory receives only the immutable view the request was planned against and the request's live budget. It is
/// never given caller claims, the <c>HttpContext</c>, or the inbound bearer token, and the returned client must not
/// obtain them: backend credentials are the federation's own.
/// </para>
/// <para>
/// The returned client must call <see cref="RequestBudget.BeforeBackendCall"/> before every physical send, retries
/// included, and must not send when it throws; and must call <see cref="RequestBudget.OnResponseBytesConsumed"/> for
/// each chunk of a response body it actually reads. The route never charges the budget itself. The client is used for
/// this request only and the route does not dispose it.
/// </para>
/// </remarks>
internal interface IShardClientFactory
{
    /// <summary>Creates the client for one request.</summary>
    /// <param name="view">The capabilities, and so the registry, the request was planned against.</param>
    /// <param name="budget">The request's budget; its token is also passed to every call.</param>
    /// <returns>The request's shard client.</returns>
    IShardClient CreateClient(SearchCapabilities view, RequestBudget budget);
}
