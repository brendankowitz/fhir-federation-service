using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Api.Budgets;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Api.Reads;

/// <summary>
/// The one backend operation behind the federation-proxied read <c>GET [base]/shards/{alias}/[type]/[id]</c>
/// (design spec §5.1): read one resource from the shard that owns it, using the federation's own credential.
/// </summary>
/// <remarks>
/// <para>
/// The host registers no implementation. Registering one, with <c>Federation:ProxyReads</c> on, is what enables the
/// route, the <c>read</c> capability and the search <c>alternate</c> links, all from one snapshot taken at startup.
/// </para>
/// <para>
/// <b>Input.</b> Only a validated <see cref="ShardRegistry"/> (the current tenant's), the validated
/// <see cref="ResourceKey"/>, the request's <see cref="RequestBudget"/> and its token arrive. No query string, header,
/// claim or other caller input is passed, and the binding must not accept any: the shard credential is its own and is
/// never exposed to the caller. The host has already authenticated the caller and authorised the type.
/// </para>
/// <para>
/// <b>Contract.</b> The implementation must (1) build the backend path from <see cref="ResourceKey.Type"/> and
/// <see cref="ResourceKey.Id"/> as exactly two segments and never let the transport normalise them to a different
/// endpoint; (2) honour the <c>cancellationToken</c> argument, which is <see cref="RequestBudget.Token"/>; (3) call
/// <see cref="RequestBudget.BeforeBackendCall"/> before every physical send, retry included, and
/// <see cref="RequestBudget.OnResponseBytesConsumed"/> for every chunk of response body it consumes; (4) validate the
/// backend's response shape and the parsed identity of the resource it returns (a single JSON object whose
/// <c>resourceType</c> and <c>id</c> equal the requested type and id), so the bytes it returns are the backend's exact
/// bytes and nothing else; and (5) never return or throw a backend status, header, URL or body to the host. A backend
/// refusal is reported through <see cref="ShardReadResult.NotFound"/> (404) or <see cref="ShardReadResult.Gone"/> (410);
/// any other failure of the owning shard is a <see cref="ShardFailureException"/> (or a
/// <see cref="ShardReadCircuitOpenException"/> when the shard's circuit is open), with the owner's id.
/// </para>
/// <para>Implementations are shared across requests and must be thread-safe.</para>
/// </remarks>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "Hosts and the integration test project implement this seam; the host project has no InternalsVisibleTo.")]
public interface IShardReadBinding
{
    /// <summary>Reads one resource from the shard that owns it.</summary>
    /// <param name="registry">The current tenant's registry; <paramref name="key"/>'s shard is in it.</param>
    /// <param name="key">The validated resource identity.</param>
    /// <param name="budget">The request's live budget, to charge physical sends and consumed response bytes.</param>
    /// <param name="cancellationToken">The request's token (<see cref="RequestBudget.Token"/>).</param>
    /// <returns>The resource as the owning shard holds it, or that it is absent or deleted.</returns>
    /// <exception cref="ShardFailureException">The owning shard could not be read.</exception>
    /// <exception cref="ShardReadCircuitOpenException">The owning shard's circuit is open and no call was made.</exception>
    /// <exception cref="RequestTooCostlyException">A budget charge was refused.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    ValueTask<ShardReadResult> ReadAsync(ShardRegistry registry, ResourceKey key, RequestBudget budget, CancellationToken cancellationToken);
}
