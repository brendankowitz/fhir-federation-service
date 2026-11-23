using Ignixa.Federation.Api.Authentication;
using Ignixa.Federation.Api.Authorization;
using Ignixa.Federation.Api.Budgets;
using Ignixa.Federation.Api.Search;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Net.Http.Headers;

namespace Ignixa.Federation.Api.Reads;

/// <summary>
/// The federation-proxied read <c>GET [base]/shards/{alias}/[type]/[id]</c> (design spec §5.1) and the refusal of the
/// unqualified <c>GET [base]/[type]/[id]</c>, which cannot name an owner.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enablement.</b> One snapshot, taken when the search is built: <c>Federation:ProxyReads</c> on <i>and</i> an
/// <see cref="IShardReadBinding"/> registered. Search alternate links, the <c>read</c> capability and the route all
/// follow <see cref="Enabled"/>, so they cannot disagree. A disabled route answers 404 only after authentication and
/// type authorisation, and does no binding I/O.
/// </para>
/// <para>
/// <b>Order.</b> Budget and signing freshness, authentication, the caller's type grant, enablement, the query, the
/// alias (resolved in this tenant's registry only), the id, and only then the binding, which receives the validated
/// registry and key, the budget and its token and nothing from the caller. A cost breach, caller cancellation and the
/// deadline are all checked before any result is returned and outrank every backend outcome; the status, headers and
/// body are written only for a result that survived that last checkpoint.
/// </para>
/// <para>Thread-safe: immutable after construction.</para>
/// </remarks>
internal sealed class ProxiedReads
{
    private const string Scheme = FederationCallerAuthentication.SchemeName;
    private const string ContentType = "application/fhir+json; charset=utf-8";
    private const string NotFoundText = "The resource was not found.";

    private readonly SearchHostSettings _settings;
    private readonly ShardRegistry _registry;
    private readonly IShardReadBinding? _binding;

    /// <summary>Initializes a new instance of the <see cref="ProxiedReads"/> class.</summary>
    /// <param name="settings">The validated host settings.</param>
    /// <param name="registry">The current tenant's registry.</param>
    /// <param name="binding">The registered binding when the proxy is enabled by configuration; otherwise <see langword="null"/>.</param>
    public ProxiedReads(SearchHostSettings settings, ShardRegistry registry, IShardReadBinding? binding)
    {
        _settings = settings;
        _registry = registry;
        _binding = binding;
    }

    /// <summary>Gets a value indicating whether the proxied read is enabled.</summary>
    public bool Enabled => _binding is not null;

    /// <summary>Gets the trusted base the search <c>alternate</c> links are built from, or <see langword="null"/> when disabled.</summary>
    public Uri? AlternateBase => Enabled ? new Uri(_settings.PublicBase) : null;

    /// <summary>The answer to <c>GET [type]/[id]</c>: ambiguous across owners, so never looked up.</summary>
    /// <param name="proxyEnabled">Whether to point at the proxy path as well as at the entry's <c>fullUrl</c>.</param>
    /// <returns>A 400 <c>OperationOutcome</c>.</returns>
    public static IResult Ambiguous(bool proxyEnabled) => SearchOutcome.Invalid(
        "An unqualified read is ambiguous: more than one owner can hold the same type and id. "
        + "Read the resource at the fullUrl of its search entry"
        + (proxyEnabled ? ", or at the /shards/{alias}/{type}/{id} path of its alternate link." : "."));

    /// <summary>Serves one proxied read.</summary>
    /// <param name="http">The request.</param>
    /// <param name="alias">The raw alias segment.</param>
    /// <param name="type">The type segment, already accepted by <see cref="ResourceTypeRouteConstraint"/>.</param>
    /// <param name="id">The raw id segment.</param>
    /// <param name="request">The budget and signing freshness bound established before authentication.</param>
    /// <returns>The resource, a refusal, or a failure; nothing is written before the result is final.</returns>
    /// <exception cref="OperationCanceledException">The caller aborted the request.</exception>
    public async Task<IResult> ReadAsync(HttpContext http, string alias, string type, string id, PrimarySearchEndpoints.SearchRequest request)
    {
        RequestBudget budget = request.Budget;
        try
        {
            try
            {
                IResult result = await ServeAsync(http, alias, ResourceType.Parse(type), id, request);
                budget.Checkpoint();
                return result;
            }
            catch
            {
                // A stop (cost, caller, deadline) outranks whatever the backend reported or threw.
                budget.Checkpoint();
                throw;
            }
        }
        catch (RequestTooCostlyException)
        {
            return SearchOutcome.TooCostly();
        }
        catch (RequestDeadlineExceededException)
        {
            http.Response.Headers.Remove(HeaderNames.WWWAuthenticate);
            return SearchOutcome.TimedOut();
        }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
        {
            // Not the caller's and the budget is clean: a backend call's own timeout.
            return SearchOutcome.Error(StatusCodes.Status504GatewayTimeout, "timeout", "The owning shard did not respond in time.");
        }
        catch (ShardFailureException ex)
        {
            string owner = _registry.TryGetById(ex.Shard, out ShardDescriptor? shard) ? shard.Alias.Value : "unknown";
            return ex.Reason == ShardFailureReason.Timeout
                ? SearchOutcome.Error(StatusCodes.Status504GatewayTimeout, "timeout", $"Shard {owner} did not respond in time.")
                : SearchOutcome.Error(StatusCodes.Status502BadGateway, "exception", $"Shard {owner} failed.");
        }
        catch (ShardReadCircuitOpenException ex)
        {
            string owner = _registry.TryGetById(ex.Shard, out ShardDescriptor? shard) ? shard.Alias.Value : "unknown";
            return SearchOutcome.Unavailable($"Shard {owner} is temporarily unavailable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The binding contract forbids anything else; whatever it was, none of it reaches the caller.
            return SearchOutcome.Error(StatusCodes.Status502BadGateway, "exception", "The owning shard failed.");
        }
    }

    private async Task<IResult> ServeAsync(HttpContext http, string alias, ResourceType type, string id, PrimarySearchEndpoints.SearchRequest request)
    {
        AuthenticateResult authentication = await http.AuthenticateAsync(Scheme);
        RequestBudget budget = request.Budget;
        budget.Checkpoint();
        if (request.CheckSigning() is { } unavailable)
        {
            return unavailable;
        }

        CallerAccess access = CallerAccess.Evaluate(authentication, Scheme, _settings.RoleGrants);
        if (access.FailureStatusCode == StatusCodes.Status401Unauthorized)
        {
            await http.ChallengeAsync(Scheme);
            return SearchOutcome.Error(StatusCodes.Status401Unauthorized, "login", "A valid federation caller token is required.");
        }

        if (access.FailureStatusCode is not null || !access.CanRead(type))
        {
            return SearchOutcome.Error(StatusCodes.Status403Forbidden, "forbidden", "The caller may not read this resource type.");
        }

        if (_binding is null)
        {
            return NotFound();
        }

        if (RefuseQuery(http.Request.Query) is { } refused)
        {
            return refused;
        }

        if (!ShardAlias.TryParse(alias, out ShardAlias? owner) || !_registry.TryGetByAlias(owner, out ShardDescriptor? shard))
        {
            return NotFound();
        }

        // "." and ".." are lexically valid ids but name no resource, and a transport may normalise them to another endpoint.
        if (id is "." or ".." || !ResourceId.TryParse(id, out ResourceId? resourceId))
        {
            return SearchOutcome.Invalid("The resource id is not valid.");
        }

        var key = new ResourceKey(shard.Id, type, resourceId);
        ShardReadResult? read = await _binding.ReadAsync(_registry, key, budget, budget.Token);
        if (read is null)
        {
            return BadGateway(shard);
        }

        if (read == ShardReadResult.NotFound)
        {
            return NotFound();
        }

        if (read == ShardReadResult.Gone)
        {
            return SearchOutcome.Error(StatusCodes.Status410Gone, "deleted", "The resource has been deleted.");
        }

        ShardEntry entry = read.Entry!;
        return entry.Key.Equals(key) ? new Found(_registry.FullUrl(key), entry.Resource) : BadGateway(shard);
    }

    private static IResult NotFound() => SearchOutcome.Error(StatusCodes.Status404NotFound, "not-found", NotFoundText);

    private static IResult BadGateway(ShardDescriptor shard) =>
        SearchOutcome.Error(StatusCodes.Status502BadGateway, "exception", $"Shard {shard.Alias.Value} failed.");

    /// <summary>Only <c>_format</c>, once and as JSON, may accompany a read; nothing else is accepted or forwarded.</summary>
    private static IResult? RefuseQuery(IQueryCollection query)
    {
        foreach (string key in query.Keys)
        {
            if (key != "_format")
            {
                return SearchOutcome.NotSupported(StatusCodes.Status400BadRequest, "Only the _format parameter is supported on a read.");
            }
        }

        if (!query.TryGetValue("_format", out var format))
        {
            return null;
        }

        if (format.Count != 1)
        {
            return SearchOutcome.Invalid("Supply at most one _format value.");
        }

        // Query decoding turns an unescaped '+' into a space, as on the search and metadata routes.
        return format[0] is "json" or "application/json" or "application/fhir+json" or "application/fhir json"
            ? null
            : SearchOutcome.NotSupported(StatusCodes.Status415UnsupportedMediaType, "Only JSON is supported.");
    }

    /// <summary>A complete, final read; it writes the header and the owner's exact bytes only when executed.</summary>
    private sealed class Found(string contentLocation, ReadOnlyMemory<byte> resource) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.ContentLocation = contentLocation;
            return Results.Bytes(resource, ContentType).ExecuteAsync(httpContext);
        }
    }
}
