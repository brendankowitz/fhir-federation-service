using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Claims;
using System.Text;
using Ignixa.Federation.Api.Authentication;
using Ignixa.Federation.Api.Authorization;
using Ignixa.Federation.Api.Budgets;
using Ignixa.Federation.Api.Continuations;
using Ignixa.Federation.Api.Reads;
using Ignixa.Federation.Api.Responses;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Counts;
using Ignixa.Federation.Core.Execution.ReverseChains;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Ignixa.Federation.Api.Search;

/// <summary>
/// The public primary search: <c>GET [type]</c>, <c>POST [type]/_search</c> and <c>GET [type]?ct=…</c>, one
/// authenticated path from caller to shards and back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Unsorted pages without a total support ordinary predicates, certified distributed reverse chains and
/// depth-one native forward chains the operator listed as passthrough paths. A passthrough forward chain keeps its raw
/// occurrences and each owner joins locally, as an operator promise for that path only; the caller must be able to read the
/// target type. Native forward chains do not combine with reverse chains in one search.
/// One distributed depth-one forward chain with only ordinary predicates executes through the optional
/// <see cref="IDistributedForwardSearch"/>: its resolver gets this request's one budget-bound client, and every owner then pages
/// its own canonical rewrite of the chain, never the raw chain. A host without that strategy refuses it before any client exists.
/// Ordinary count-only searches require exact shape proof and an explicit dialect for each shard. Count-only searches
/// with admitted distributed reverse chains resolve witnesses and then count every owner's generated request through the
/// same exact executor, within the remaining physical allowance. Ordinary unsorted paged <c>_revinclude</c> is staged by
/// the optional <see cref="IRevIncludePageResolver"/> from exactly the emitted page; it does not combine with a chain, a
/// total, a count, a sort or <c>_include</c>. Other chains, includes, sort and paged totals are not executed.
/// </para>
/// <para>
/// <b>Continuations.</b> A token carries the stored original raw query and the executor's snapshot. Redemption
/// recognises <c>ct</c> before consulting the view, then replans that query on this host's view and requires exactly
/// the saved shard requests and page size. Expiry or relevant registry or plan drift is 410; unreadable authenticated
/// bytes or an inconsistent dependency manifest are 400, all before any shard call. An anchor found missing during
/// replay is 410 only while the request budget is live. Each admitted predicate pins its resolved definition's revision
/// (<see cref="ResolvedDefinitionRevisions.GetResolvedDefinitionRevision"/>), independent of the discovery projection;
/// only those definitions are compared. A forward chain pins its source reference and its target terminal definitions,
/// and redemption plans it again: a path no longer certified natively is 410, and the target-type permission is rechecked.
/// Reverse-chain continuations also pin the witness definitions and all discovery
/// shards, recheck source-type permission, and replay frozen owner streams without repeating witness discovery.
/// A staged page with an already expired next link is 410 with no Bundle.
/// A next link that would not fit this server's own query or request-line
/// limits, or an inline token, is 503, never a truncated or partial page.
/// </para>
/// <para>
/// <b>Failure.</b> One <see cref="RequestBudget"/> spans authentication, body read, planning, shard work and
/// materializing the page. Discovery runs separately from requests. Every failure is a whole-request <c>OperationOutcome</c>
/// with fixed text that names no query value, token or backend detail; a failing shard is named only by its alias.
/// Caller cancellation propagates.
/// </para>
/// <para>Thread-safe: every field is immutable after construction; per-request state lives on the stack.</para>
/// </remarks>
internal sealed class PrimarySearch
{
    private const string Scheme = FederationCallerAuthentication.SchemeName;
    private const int MaxRawQueryLength = 8 * 1024;
    private const int MaxBodyBytes = 32 * 1024;
    private const string FormMediaType = "application/x-www-form-urlencoded";

    /// <summary>The continuation parameter, matched on the decoded key exactly as <see cref="SearchPlanner"/> does.</summary>
    private const string ContinuationKey = "ct";

    private static readonly IReadOnlyDictionary<int, string> NoReplacements = FrozenDictionary<int, string>.Empty;

    /// <summary>The claims that may carry the caller's client identifier, all of which must agree.</summary>
    private static readonly string[] ClientIdClaims = ["client_id", "azp", "appid"];

    private readonly SearchHostSettings _settings;
    private readonly SearchCapabilities _capabilities;
    private readonly ShardRegistry _registry;
    private readonly LimitOptions _limits;
    private readonly TimeoutOptions _timeouts;
    private readonly SearchPlanner _planner;
    private readonly ContinuationProtector _protector;
    private readonly UnsortedSnapshotCodec _codec;
    private readonly IShardClientFactory _clients;
    private readonly TimeProvider _time;
    private readonly int _maxRequestLineBytes;
    private readonly FrozenDictionary<string, string> _shardRevisions;
    private readonly FrozenDictionary<ShardId, CountDialect>? _countDialects;
    private readonly IRevIncludePageResolver? _revIncludes;
    private readonly IDistributedForwardSearch? _forward;

    /// <summary>Gets the proxied read, built from the same enablement snapshot the search links and capability statement follow.</summary>
    public ProxiedReads Reads { get; }

    private PrimarySearch(
        SearchHostSettings settings,
        SearchCapabilities capabilities,
        FederationOptions options,
        IShardClientFactory clients,
        FrozenDictionary<ShardId, CountDialect>? countDialects,
        IShardReadBinding? readBinding,
        IRevIncludePageResolver? revIncludes,
        IDistributedForwardSearch? forward,
        IDataProtectionProvider protection,
        TimeProvider time,
        int maxRequestLineBytes)
    {
        _revIncludes = revIncludes;
        _forward = forward;
        _settings = settings;
        _capabilities = capabilities;
        _registry = capabilities.Registry;
        Reads = new ProxiedReads(settings, _registry, options.ProxyReads ? readBinding : null);
        _limits = options.Limits;
        _timeouts = options.Timeouts;
        _planner = new SearchPlanner(capabilities, options.ChainResolution, options.Limits);
        _protector = new ContinuationProtector(protection, options.Tokens, time);
        _codec = new UnsortedSnapshotCodec(options.Tokens.MaxInlineTokenBytes, _registry);
        _clients = clients;
        _countDialects = countDialects;
        _time = time;
        _maxRequestLineBytes = maxRequestLineBytes;

        _shardRevisions = _registry.Shards
            .ToFrozenDictionary(s => s.Id.Value, s => _registry.ComputeVersion([s.Id]), StringComparer.Ordinal);
    }

    /// <summary>Creates the budget owned by the search middleware for this request.</summary>
    public RequestBudget StartBudget(CancellationToken caller) =>
        new(_limits, _timeouts, _registry.Shards.Count, _time, caller);

    /// <summary>Builds the search from a capabilities view, the registered transport and the startup <c>Federation</c> options.</summary>
    /// <remarks>
    /// To enable count-only searches, register an <see cref="IReadOnlyDictionary{TKey,TValue}"/> from <see cref="ShardId"/>
    /// to <see cref="CountDialect"/> for that view. A missing or invalid map disables count execution, including distributed
    /// reverse-chain counts. It also gates paged reverse-chain entry and continuation redemption: those paths only need the
    /// executor's explicit dialect map wired, to measure generated requests, and never execute counts.
    /// Paged <c>_revinclude</c> additionally requires an explicitly registered <see cref="IRevIncludePageResolver"/>;
    /// without one such a search is a valid but unsupported request.
    /// </remarks>
    /// <param name="services">The application services.</param>
    /// <param name="settings">The validated host settings.</param>
    /// <param name="options">The <c>Federation</c> options bound from the startup snapshot; checked here against the view's profiles.</param>
    /// <param name="capabilities">The view this search plans against; the same instance the capability statement describes.</param>
    /// <returns>The search.</returns>
    /// <exception cref="InvalidOperationException">
    /// No transport is registered, the <c>Federation</c> options are invalid, or its shards do not describe
    /// exactly the view's registry. Messages carry only configuration paths.
    /// </exception>
    public static PrimarySearch Create(IServiceProvider services, SearchHostSettings settings, FederationOptions options, SearchCapabilities capabilities)
    {
        IShardClientFactory clients = services.GetService<IShardClientFactory>()
            ?? throw new InvalidOperationException("Search is configured but no shard transport (IShardClientFactory) is registered.");

        BackendProfile[] profiles = [.. capabilities.Registry.Shards.Select(s => s.Profile).DistinctBy(p => p.Name)];
        ValidateOptionsResult validation = new FederationOptionsValidator(profiles).Validate(null, options);
        if (validation.Failed)
        {
            throw new InvalidOperationException(
                "The Federation options are invalid:" + Environment.NewLine + string.Join(Environment.NewLine, validation.Failures));
        }

        // The view was certified against one registry; the configured shards must describe exactly that registry.
        if (!ShardRegistry.TryCreate(options.Shards, profiles, out ShardRegistry? configured, out IReadOnlyList<string> errors))
        {
            throw new InvalidOperationException(
                "The Federation shards are invalid:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }

        if (!string.Equals(configured.Version, capabilities.Registry.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Federation:Shards does not describe the registry the registered SearchCapabilities view was certified against.");
        }

        return new PrimarySearch(
            settings,
            capabilities,
            options,
            clients,
            CaptureCountDialects(capabilities.Registry, services.GetService<IReadOnlyDictionary<ShardId, CountDialect>>()),
            services.GetService<IShardReadBinding>(),
            services.GetService<IRevIncludePageResolver>(),
            services.GetService<IDistributedForwardSearch>(),
            services.GetRequiredService<IDataProtectionProvider>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestLineSize);
    }

    /// <summary>Serves one search request.</summary>
    /// <param name="http">The request.</param>
    /// <param name="route">The route's resource type, already accepted by <see cref="ResourceTypeRouteConstraint"/>.</param>
    /// <param name="post">Whether the request is <c>POST [type]/_search</c>.</param>
    /// <param name="request">The budget and signing freshness bound established before authentication.</param>
    /// <returns>The searchset page or the refusal.</returns>
    /// <exception cref="OperationCanceledException">The caller aborted the request.</exception>
    public async Task<IResult> SearchAsync(HttpContext http, string route, bool post, PrimarySearchEndpoints.SearchRequest request)
    {
        ResourceType type = ResourceType.Parse(route);

        RequestBudget budget = request.Budget;
        try
        {
            try
            {
                IResult result = await ServeAsync(http, type, post, request);
                budget.Checkpoint();
                return result;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ShardFailureException
                or ContinuationAnchorMissingException or FetchBudgetExhaustedException or TotalOverflowException)
            {
                // Prefer the live request budget to failures discovered while executing the search.
                budget.Checkpoint();
                throw;
            }
        }
        catch (Exception ex) when (ex is RequestTooCostlyException or FetchBudgetExhaustedException)
        {
            return SearchOutcome.TooCostly();
        }
        catch (RequestDeadlineExceededException)
        {
            http.Response.Headers.Remove(HeaderNames.WWWAuthenticate);
            return SearchOutcome.TimedOut();
        }
        catch (ShardFailureException ex)
        {
            string alias = _registry.TryGetById(ex.Shard, out ShardDescriptor? shard) ? shard.Alias.Value : "unknown";
            return ex.Reason == ShardFailureReason.Timeout
                ? SearchOutcome.Error(StatusCodes.Status504GatewayTimeout, "timeout", $"Shard {alias} did not respond in time.")
                : SearchOutcome.Error(StatusCodes.Status502BadGateway, "exception", $"Shard {alias} failed.");
        }
        catch (ContinuationAnchorMissingException)
        {
            return SearchOutcome.Gone();
        }
        catch (TotalOverflowException)
        {
            return SearchOutcome.Error(StatusCodes.Status502BadGateway, "exception", "The exact total cannot be represented in a searchset.");
        }
    }

    private async Task<IResult> ServeAsync(HttpContext http, ResourceType type, bool post, PrimarySearchEndpoints.SearchRequest request)
    {
        // Authenticate and authorise before reading any body or query semantics.
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

        if (access.FailureStatusCode is not null
            || Bind(authentication.Principal!.Identities.Single()) is not { } binding
            || !access.CanRead(type))
        {
            return SearchOutcome.Error(StatusCodes.Status403Forbidden, "forbidden", "The caller may not search this resource type.");
        }

        string urlQuery = http.Request.QueryString.HasValue ? http.Request.QueryString.Value![1..] : string.Empty;
        if (urlQuery.Length > MaxRawQueryLength)
        {
            return SearchOutcome.Error(StatusCodes.Status414UriTooLong, "too-long", "The query string is too long.");
        }

        string raw = urlQuery;
        if (post)
        {
            if (!IsForm(http.Request.ContentType))
            {
                return SearchOutcome.NotSupported(StatusCodes.Status415UnsupportedMediaType, "A search body must be application/x-www-form-urlencoded in UTF-8.");
            }

            byte[]? body = http.Request.ContentLength > MaxBodyBytes ? null : await ReadBodyAsync(http.Request.Body, budget.Token);
            if (body is null)
            {
                return SearchOutcome.Error(StatusCodes.Status413PayloadTooLarge, "too-long", "The search body is too large.");
            }

            if (!StrictUtf8.TryGetString(body, out string? form))
            {
                return SearchOutcome.Invalid("The search body is not valid UTF-8.");
            }

            raw = urlQuery.Length == 0 ? form : form.Length == 0 ? urlQuery : urlQuery + "&" + form;
        }

        if (!TryParse(post, raw, out SearchQuery? query, out SearchFailure? failure))
        {
            return Rejected(failure);
        }

        // A continuation is recognised before the view is consulted: whether its type and predicates still plan here is
        // decided by replanning the token's stored query on redemption (410 when they no longer do), not by this URL.
        if (query.Occurrences.FirstOrDefault(o => o.Key == ContinuationKey) is { } ct)
        {
            return query.Occurrences.Count > 1 ? SearchOutcome.Invalid("A continuation token must be the only parameter.")
                : ct.Value.Length == 0 ? SearchOutcome.Invalid("The continuation is not valid.")
                : post ? SearchOutcome.Invalid("A continuation is followed with GET, not POST.")
                : await ContinueAsync(type, access, binding, ct.Raw.Value, budget);
        }

        SearchPlanResult planned = _planner.Plan(type, query);
        if (planned.Failure is { } refused)
        {
            return Rejected(refused);
        }

        // Without a ct occurrence the planner always returns a plan.
        QueryPlan plan = planned.Plan!;
        ResourceType[] sources = ReverseSources(plan);
        if (!sources.All(access.CanRead))
        {
            return ReverseDenied();
        }

        if (!ForwardTargets(plan).All(access.CanRead))
        {
            return ForwardDenied();
        }

        if (plan.CountOnly && plan.Occurrences.All(o => (o.Kind, o.Action) is
            (OccurrenceKind.Control, OccurrenceAction.Remove) or (OccurrenceKind.Predicate, OccurrenceAction.Keep)
            or (OccurrenceKind.ReverseChain, OccurrenceAction.Replace)))
        {
            return await ExecuteCountAsync(plan, sources, post, raw, budget);
        }

        if (!TryPrepare(post, raw, plan, sources.Length != 0, out PreparedSearch? prepared))
        {
            return UnsupportedShape();
        }

        // Fixed by plan and host composition, so a host without the strategy refuses before any client or shard exists.
        if (IsDistributedForward(plan))
        {
            return _forward is { } forward
                ? await ExecuteForwardAsync(plan, forward, binding, prepared, budget)
                : UnsupportedShape();
        }
        // Fixed by grants and plan, so a host without the resolver refuses before any client or shard exists.
        RevIncludeStage? includes = RevIncludeStage.Create(plan, access);
        if (includes is not null && _revIncludes is null)
        {
            return UnsupportedShape();
        }

        if (sources.Length == 0)
        {
            return await ExecuteAsync(type, binding, prepared, predecessor: null, resumeFrom: null, budget, includes: includes);
        }

        return _countDialects is { } dialects
            ? await ExecuteReverseAsync(plan, sources, binding, prepared, dialects, budget)
            : UnsupportedShape();
    }

    private static IResult Rejected(SearchFailure failure) =>
        failure.Code == SearchFailureCode.UnsupportedFormat
            ? SearchOutcome.NotSupported(StatusCodes.Status415UnsupportedMediaType, failure.Message)
            : failure.IsNotSupported
                ? SearchOutcome.NotSupported(StatusCodes.Status400BadRequest, failure.Message)
                : SearchOutcome.Invalid(failure.Message);

    private static FrozenDictionary<ShardId, CountDialect>? CaptureCountDialects(
        ShardRegistry registry, IReadOnlyDictionary<ShardId, CountDialect>? registered)
    {
        if (registered is null)
        {
            return null;
        }

        // Validate the same ordinal shard identities and values that will actually be used for requests.
        var copy = new Dictionary<ShardId, CountDialect>();
        foreach ((ShardId shard, CountDialect dialect) in registered)
        {
            if (shard is null || !registry.TryGetById(shard, out _) || !Enum.IsDefined(dialect) || !copy.TryAdd(shard, dialect))
            {
                return null;
            }
        }

        return copy.Count == registry.Shards.Count ? copy.ToFrozenDictionary() : null;
    }

    private async Task<IResult> ExecuteCountAsync(QueryPlan plan, ResourceType[] sources, bool post, string raw, RequestBudget budget)
    {
        if (_countDialects is not { } dialects)
        {
            return SearchOutcome.NotSupported(StatusCodes.Status400BadRequest, "An exact count is not configured on every shard.");
        }

        budget.Checkpoint();
        IShardClient client = _clients.CreateClient(_capabilities, budget);
        var executor = new FederatedCountExecutor(client, _capabilities, dialects, _limits.MaxParallelBackendCalls);
        ShardCountTarget[] targets;
        int allowance = FetchAllowance;
        if (sources.Length == 0)
        {
            targets = [.. _registry.Shards.Select(s => ShardCountTarget.Search(s.Id, SearchMethod(post, s), NoReplacements))];
        }
        else
        {
            (ReverseChainResolution? resolved, IResult? refused) = await ResolveReverseAsync(plan, sources, executor, client, budget);
            if (resolved is null)
            {
                return refused!;
            }

            // Every shard is a target: the shards the resolver found no candidate on are proven empty, never skipped.
            budget.Checkpoint();
            targets =
            [
                .. _registry.Shards.Select(s => resolved.Targets.FirstOrDefault(t => t.Shard == s.Id) is { } t
                    ? ShardCountTarget.Search(s.Id, t.Method, t.Replacements)
                    : ShardCountTarget.ProvenEmpty(s.Id)),
            ];
            allowance = (int)Math.Max(0, FetchAllowance - Math.Max(budget.BackendCalls, resolved.Calls));
        }

        int total;
        try
        {
            total = await executor.ExecuteAsync(plan, targets, allowance, budget.Token)
                ?? throw new InvalidOperationException("The count-only plan returned no total.");
        }
        catch (ExactCountNotCertifiedException)
        {
            return SearchOutcome.NotSupported(StatusCodes.Status400BadRequest, "An exact count is not certified for this query on every shard.");
        }

        budget.Checkpoint();
        return SearchsetResponse.Create(_registry, SelfLink(plan.Type, raw), [], [], [], total: total);
    }

    private async Task<(ReverseChainResolution? Resolved, IResult? Refused)> ResolveReverseAsync(
        QueryPlan plan, ResourceType[] sources, FederatedCountExecutor counts, IShardClient client, RequestBudget budget)
    {
        var resolver = new ReverseChainResolver(client, _capabilities, counts, _clients.FitsSearchRequest,
            _limits.MaxChainKeys, _limits.MaxChainSourceRows, _limits.MaxParallelBackendCalls, _timeouts.ChainSubquery);
        var authorizedTypes = new HashSet<ResourceType>(sources) { plan.Type };
        try
        {
            return (await resolver.ResolveAsync(plan, authorizedTypes, FetchAllowance, budget.Token), null);
        }
        catch (ReverseChainRefusedException ex)
        {
            return (null, ex.Reason switch
            {
                ReverseChainRefusal.TooCostly => SearchOutcome.TooCostly(),
                ReverseChainRefusal.NotCertified => UnsupportedShape(),
                ReverseChainRefusal.UnauthorizedType => ReverseDenied(),
                _ => throw new InvalidOperationException("Unhandled reverse-chain refusal."),
            });
        }
        catch (TimeoutException)
        {
            return (null, SearchOutcome.TimedOut());
        }
    }

    private async Task<IResult> ExecuteReverseAsync(QueryPlan plan, ResourceType[] sources, ContinuationBinding binding,
        PreparedSearch search, FrozenDictionary<ShardId, CountDialect> dialects, RequestBudget budget)
    {
        budget.Checkpoint();
        IShardClient client = _clients.CreateClient(_capabilities, budget);
        var counts = new FederatedCountExecutor(client, _capabilities, dialects, _limits.MaxParallelBackendCalls);
        (ReverseChainResolution? resolved, IResult? refused) = await ResolveReverseAsync(plan, sources, counts, client, budget);
        if (resolved is null)
        {
            return refused!;
        }

        budget.Checkpoint();
        if (resolved.Targets.Count == 0)
        {
            return SearchsetResponse.Create(_registry, SelfLink(plan.Type, search.RawQuery), [], [], []);
        }

        return await ExecuteAsync(plan.Type, binding,
            search with { Streams = [.. resolved.Targets.Select(t => t.Request(search.PageSize))] },
            predecessor: null, resumeFrom: null, budget, client,
            (int)Math.Max(0, FetchAllowance - Math.Max(budget.BackendCalls, resolved.Calls)));
    }

    /// <summary>
    /// Resolves one distributed forward chain with the host's strategy on the request's single client, then pages exactly the
    /// canonical owner streams it returned.
    /// </summary>
    /// <remarks>
    /// The strategy is trusted for what a witness means, not for what the host may send: its streams must be the canonical
    /// boundary shape and every one must fit the transport before any owner is asked for a page. Budget, deadline and shard
    /// failures propagate to <see cref="SearchAsync"/>, which classifies them; none yields a partial page.
    /// </remarks>
    private async Task<IResult> ExecuteForwardAsync(
        QueryPlan plan, IDistributedForwardSearch forward, ContinuationBinding binding, PreparedSearch search, RequestBudget budget)
    {
        budget.Checkpoint();
        IShardClient client = _clients.CreateClient(_capabilities, budget);
        var readable = new HashSet<ResourceType>(ForwardTargets(plan)) { plan.Type };

        // The logical allowance and the physical one coincide at entry; later the shared budget's own count is the bound.
        int allowance = RemainingPhysicalFetches(budget);
        long payloadAllowance = Math.Max(0, _limits.MaxResponseBytes - budget.ResponseBytes);
        (IReadOnlyList<ShardSearchRequest> Streams, int RemainingFetches)? resolved;
        try
        {
            resolved = await forward.ResolveAsync(
                plan, _capabilities, readable, client, _limits, _timeouts, _clients.FitsSearchRequest, allowance, payloadAllowance, budget.Token);
        }
        catch (TimeoutException)
        {
            budget.Checkpoint();
            return SearchOutcome.TimedOut();
        }

        budget.Checkpoint();
        if (resolved is null)
        {
            return SearchOutcome.NotSupported(StatusCodes.Status400BadRequest, "The required reference forms are not certified for this chain.");
        }

        (IReadOnlyList<ShardSearchRequest> streams, int remainingFetches) = resolved.Value;

        if (streams.Count == 0)
        {
            return SearchsetResponse.Create(_registry, SelfLink(plan.Type, search.RawQuery), [], [], []);
        }

        if (remainingFetches < 0
            || remainingFetches > allowance
            || !DistributedForwardStreams.IsCanonical(_registry, plan, search.PageSize, streams, resumed: null))
        {
            return StrategyViolation();
        }

        if (!DistributedForwardStreams.AllFit(_registry, streams, _clients.FitsSearchRequest))
        {
            return SearchOutcome.TooCostly();
        }

        return await ExecuteAsync(plan.Type, binding, search with { Streams = [.. streams] },
            predecessor: null, resumeFrom: null, budget, client, Math.Min(remainingFetches, RemainingPhysicalFetches(budget)));
    }

    private int RemainingPhysicalFetches(RequestBudget budget) => (int)Math.Max(0, FetchAllowance - budget.BackendCalls);

    private static IResult StrategyViolation() =>
        SearchOutcome.Error(StatusCodes.Status500InternalServerError, "exception", "The distributed forward strategy returned streams this host cannot execute.");

    private async Task<IResult> ContinueAsync(ResourceType type, CallerAccess access, ContinuationBinding binding, string token, RequestBudget budget)
    {
        // Only expiry, relevant registry or plan drift, and a missing anchor are 410; authentic bytes this host cannot
        // read (malformed, or another frame or snapshot version) are 400, per the snapshot codec's outcomes.
        ContinuationRedemption redemption = _protector.Redeem(token, binding, CurrentRevision);
        switch (redemption.Outcome)
        {
            case ContinuationRedemptionOutcome.Redeemed:
                break;
            case ContinuationRedemptionOutcome.CallerMismatch:
                return SearchOutcome.Error(StatusCodes.Status403Forbidden, "forbidden", "The continuation belongs to another caller.");
            case ContinuationRedemptionOutcome.Gone:
                return SearchOutcome.Gone();
            case ContinuationRedemptionOutcome.Invalid:
                return SearchOutcome.Invalid("The continuation is not valid.");
            default:
                throw new InvalidOperationException("Unhandled continuation redemption outcome.");
        }

        RedeemedContinuation predecessor = redemption.Continuation!;
        if (SearchContinuationFrame.TryRead(predecessor.Snapshot.AsSpan(), out SearchContinuationFrame? frame) != UnsortedSnapshotOutcome.Success)
        {
            return SearchOutcome.Invalid("The continuation is not valid.");
        }

        switch (_codec.Decode(frame!.Snapshot, out UnsortedContinuationSnapshot? snapshot))
        {
            case UnsortedSnapshotOutcome.Success:
                break;
            case UnsortedSnapshotOutcome.Gone:
                return SearchOutcome.Gone();
            default:
                return SearchOutcome.Invalid("The continuation is not valid.");
        }

        if (snapshot!.Streams[0].Type != type)
        {
            return SearchOutcome.Invalid("The continuation belongs to another resource type.");
        }

        // Replan the stored query on this host's view: it must admit exactly the saved shard requests. A stored query
        // is never itself a continuation, so anything but a plan is drift.
        if (!TryParse(frame.Post, frame.RawQuery, out SearchQuery? stored, out _)
            || _planner.Plan(type, stored).Plan is not { } plan)
        {
            return SearchOutcome.Gone();
        }

        ResourceType[] sources = ReverseSources(plan);
        if (!sources.All(access.CanRead))
        {
            return ReverseDenied();
        }

        if (!ForwardTargets(plan).All(access.CanRead))
        {
            return ForwardDenied();
        }

        bool reverse = sources.Length != 0;
        if (!TryPrepare(frame.Post, frame.RawQuery, plan, reverse, out PreparedSearch? prepared)
            || prepared.PageSize != snapshot.PageSize)
        {
            return SearchOutcome.Gone();
        }

        if (IsDistributedForward(plan))
        {
            // Everything that can disagree with the current plan, grants, proof, registry or limits is decided here, purely
            // and before any client exists. Witness discovery is never repeated: the saved rewrites are the evidence.
            if (_forward is null)
            {
                return SearchOutcome.Gone();
            }

            IReadOnlyList<ShardSearchRequest>? rebuilt = _forward.Rebuild(
                plan, _capabilities, snapshot.Streams, _limits.MaxChainKeys, _clients.FitsSearchRequest);
            if (rebuilt is null
                || !DistributedForwardStreams.IsCanonical(_registry, plan, prepared.PageSize, rebuilt, snapshot.Continuation)
                || !DistributedForwardStreams.AllFit(_registry, rebuilt, _clients.FitsSearchRequest))
            {
                return SearchOutcome.Gone();
            }

            prepared = prepared with { Streams = [.. rebuilt] };
        }
        else if (reverse)
        {
            if (_countDialects is null)
            {
                return UnsupportedShape();
            }

            if (!ReverseChainReplay.TryRebuild(_registry, plan, snapshot, _limits.MaxChainKeys, out ShardSearchRequest[]? rebuilt))
            {
                return SearchOutcome.Gone();
            }

            prepared = prepared with { Streams = rebuilt };
        }

        if (!prepared.Streams.SequenceEqual(snapshot.Streams, SameRequest.Instance))
        {
            return SearchOutcome.Gone();
        }

        // Redemption proved the carried revisions still current, not that they cover the whole witness registry.
        // An intact old manifest can therefore be stale even when the owner streams still match.
        if (!SameDependencies(predecessor.Dependencies, Dependencies(prepared)))
        {
            return reverse && WitnessRegistryGrew(predecessor.Dependencies, prepared)
                ? SearchOutcome.Gone()
                : SearchOutcome.Invalid("The continuation is not valid.");
        }

        // Genuine plan, proof or registry drift was decided above; a host that cannot stage includes is merely unconfigured.
        RevIncludeStage? includes = RevIncludeStage.Create(plan, access);
        if (includes is not null && _revIncludes is null)
        {
            return UnsupportedShape();
        }

        return await ExecuteAsync(type, binding, prepared, predecessor, snapshot.Continuation, budget, includes: includes);
    }

    /// <summary>Runs the shards for one page, links its successor, and materializes the response within the deadline.</summary>
    /// <remarks>Budget, deadline and shard failures propagate to <see cref="SearchAsync"/>, which classifies them.</remarks>
    private async Task<IResult> ExecuteAsync(
        ResourceType type,
        ContinuationBinding binding,
        PreparedSearch search,
        RedeemedContinuation? predecessor,
        UnsortedContinuation? resumeFrom,
        RequestBudget budget,
        IShardClient? client = null,
        int? remainingFetches = null,
        RevIncludeStage? includes = null)
    {
        // A request whose deadline passed while its body was read or planned never reaches a shard.
        budget.Checkpoint();
        client ??= _clients.CreateClient(_capabilities, budget);
        var executor = new UnsortedPageExecutor(client, _registry, _limits.MaxParallelBackendCalls);
        UnsortedPage page = await executor.ExecuteAsync(search.Streams, search.PageSize, resumeFrom, remainingFetches ?? FetchAllowance, budget.Token);

        // A page that exceeded the cost budget is too costly, not a page with an unlinkable successor.
        budget.Checkpoint();

        // The page is complete here: its includes are staged from exactly its matches, before any successor is issued.
        StagedIncludes staged = includes is null ? StagedIncludes.None : await StageIncludesAsync(includes, page, client, budget, predecessor is not null);
        if (staged.Refusal is { } refusal)
        {
            return refusal;
        }

        Uri? next = null;
        long? nextDeadlineTicks = null;
        if (page.Next is { } continuation)
        {
            UnsortedSnapshotOutcome encoded = _codec.Encode(
                new UnsortedContinuationSnapshot(search.Streams, search.PageSize, continuation),
                out byte[]? snapshot);
            if (encoded == UnsortedSnapshotOutcome.TooLarge)
            {
                return NextTooLarge();
            }

            if (encoded != UnsortedSnapshotOutcome.Success)
            {
                throw new InvalidOperationException("The executor's continuation could not be encoded.");
            }

            byte[] frame = new SearchContinuationFrame(search.Post, search.RawQuery, snapshot!).ToArray();
            ContinuationIssue issue = predecessor is null
                ? _protector.Issue(binding, Dependencies(search), frame)
                : _protector.IssueSuccessor(predecessor, frame);
            if (issue.InlineToken is not { } token)
            {
                return NextTooLarge();
            }

            next = new Uri($"{_settings.PublicBase}/{type.Value}?{ContinuationKey}={token}");
            if (!FitsIngress(next))
            {
                return NextTooLarge();
            }

            nextDeadlineTicks = issue.DeadlineTicks;
        }

        IResult response;
        try
        {
            response = SearchsetResponse.Create(_registry, SelfLink(type, search.RawQuery), page.Matches, staged.Includes, staged.Outcomes, next, Reads.AlternateBase);
        }
        catch (SearchsetEntryException ex) when (_registry.TryGetById(ex.Key.Shard, out ShardDescriptor? shard))
        {
            return SearchOutcome.Error(StatusCodes.Status502BadGateway, "exception", $"Shard {shard.Alias.Value} returned a resource the federation cannot serve.");
        }
        catch (ArgumentException)
        {
            return SearchOutcome.Error(StatusCodes.Status502BadGateway, "exception", "A shard returned a resource the federation cannot serve.");
        }

        if (nextDeadlineTicks is long deadline && _time.GetUtcNow().UtcTicks >= deadline)
        {
            return SearchOutcome.Gone();
        }

        return response;
    }

    private static IResult NextTooLarge() =>
        SearchOutcome.Unavailable("The next page cannot be linked: its continuation does not fit in a link this server accepts.");

    /// <summary>
    /// Resolves the reverse includes of exactly the emitted page, on the request's own client and within what its budget has left.
    /// </summary>
    /// <remarks>
    /// The resolver is never asked when no requested source type is readable or the page has no match; a denied type is
    /// reported by one fixed outcome whatever the data holds. A <see langword="null"/> result is insufficient evidence, not
    /// an empty include set: a 400 not-supported outcome for a new search, or a 410 restart for a continuation.
    /// </remarks>
    private async Task<StagedIncludes> StageIncludesAsync(RevIncludeStage stage, UnsortedPage page, IShardClient client, RequestBudget budget, bool redeemed)
    {
        List<ReadOnlyMemory<byte>> outcomes = [];
        IReadOnlyList<ShardEntry> entries = [];
        if (stage.Sources.Count != 0 && page.Matches.Count != 0)
        {
            int remaining = (int)Math.Max(0, FetchAllowance - Math.Max(budget.BackendCalls, page.Fetches));
            RevIncludePageResult? result = await _revIncludes!.ResolveAsync(
                stage.Plan,
                page.Matches,
                stage.Readable,
                client,
                _registry,
                _limits.MaxParallelBackendCalls,
                _clients.FitsSearchRequest,
                _limits.MaxIncludesPerPage,
                remaining,
                budget.Token);

            // Work the resolver did past the budget is too costly, not a page.
            budget.Checkpoint();
            if (result is null)
            {
                return StagedIncludes.Refused(redeemed
                    ? SearchOutcome.Gone()
                    : SearchOutcome.NotSupported(StatusCodes.Status400BadRequest, "The required reverse-include reference forms are not certified."));
            }

            if (!stage.Accepts(result, _limits.MaxIncludesPerPage, page.Matches))
            {
                return StagedIncludes.Refused(SearchOutcome.Error(
                    StatusCodes.Status502BadGateway, "exception", "The included resources cannot be served."));
            }

            entries = result.Includes;
            if (result.Truncated)
            {
                outcomes.Add(IncludeOutcomes.Incomplete);
            }
        }

        if (stage.Denied)
        {
            outcomes.Add(IncludeOutcomes.Suppressed);
        }

        return new StagedIncludes(entries, outcomes, null);
    }

    /// <summary>The includes and outcomes of one page, or the refusal that replaces it.</summary>
    private sealed record StagedIncludes(IReadOnlyList<ShardEntry> Includes, IReadOnlyList<ReadOnlyMemory<byte>> Outcomes, IResult? Refusal)
    {
        public static StagedIncludes None { get; } = new([], [], null);

        public static StagedIncludes Refused(IResult refusal) => new([], [], refusal);
    }

    /// <summary>
    /// Whether a client following <paramref name="next"/> would get past this server's own ingress: the query limit
    /// <see cref="ServeAsync"/> applies, and Kestrel's HTTP/1.1 request-line limit, which counts the method, the
    /// escaped origin-form path and query, the version and the terminating CRLF.
    /// </summary>
    /// <remarks>
    /// The link is built only from the configured public base, so this is the line a client sends to that base. A
    /// proxy or base path in front of the host, or HTTP/2's header-field limit on <c>:path</c>, is not modelled.
    /// </remarks>
    private bool FitsIngress(Uri next)
    {
        string query = next.Query.Length == 0 ? string.Empty : next.Query[1..];
        return query.Length <= MaxRawQueryLength
            && Encoding.UTF8.GetByteCount($"GET {next.PathAndQuery} HTTP/1.1\r\n") <= _maxRequestLineBytes;
    }

    private static string DefinitionKey(ResourceType type, string code) => $"{type.Value}.{code}";

    private static bool IsForm(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? media)
        && media.MediaType.Equals(FormMediaType, StringComparison.OrdinalIgnoreCase)
        && media.Parameters.All(p =>
            p.Name.Equals("charset", StringComparison.OrdinalIgnoreCase) && p.Value.Equals("utf-8", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads at most one byte past the limit, so an unannounced oversized body is refused without buffering it.</summary>
    /// <returns>The body, or <see langword="null"/> when it exceeds the limit.</returns>
    private static async Task<byte[]?> ReadBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[MaxBodyBytes + 1];
        int total = 0;
        int read;
        while (total < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0)
        {
            total += read;
        }

        return total > MaxBodyBytes ? null : buffer[..total];
    }

    /// <summary>Reads at most one value of the claim with exactly this name; more than one is ambiguous and fails.</summary>
    /// <remarks>
    /// Claim names compare ordinally, as <see cref="CallerAccess"/> does, whatever comparison the identity's own lookup
    /// uses: an <c>OID</c> or <c>Roles</c> claim is not the <c>oid</c> or a role.
    /// </remarks>
    private static bool TrySingle(ClaimsIdentity identity, string type, out string? value)
    {
        value = null;
        foreach (Claim claim in Named(identity, type))
        {
            if (value is not null)
            {
                return false;
            }

            value = claim.Value;
        }

        return true;
    }

    private static IEnumerable<Claim> Named(ClaimsIdentity identity, string type) =>
        identity.Claims.Where(c => string.Equals(c.Type, type, StringComparison.Ordinal));

    /// <summary>
    /// Reads the caller's client identifier from every form a token may carry it in (§7.4 <c>client_id</c>, Entra
    /// <c>azp</c> and <c>appid</c>). Every supplied form must agree, so no form silently overrides another; a
    /// disagreement fails even when an <c>oid</c> would take precedence in the binding.
    /// </summary>
    private static bool TryClientId(ClaimsIdentity identity, out string? clientId)
    {
        clientId = null;
        foreach (string type in ClientIdClaims)
        {
            if (!TrySingle(identity, type, out string? value))
            {
                return false;
            }

            if (value is null)
            {
                continue;
            }

            if (clientId is not null && !string.Equals(clientId, value, StringComparison.Ordinal))
            {
                return false;
            }

            clientId = value;
        }

        return true;
    }

    private ContinuationBinding? Bind(ClaimsIdentity identity)
    {
        if (!TrySingle(identity, "sub", out string? subject) || subject is null
            || !TrySingle(identity, "oid", out string? objectId)
            || !TryClientId(identity, out string? clientId))
        {
            return null;
        }

        try
        {
            // The issuer is pinned exactly by token validation, so the configured value is the validated one. The oid,
            // when present, takes precedence over the client identifier.
            return ContinuationBinding.Create(_settings.Issuer, subject, objectId, clientId, Named(identity, "roles").Select(c => c.Value));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Parses a search by the same rule when it arrives and when its stored query is replanned.</summary>
    private static bool TryParse(
        bool post,
        string raw,
        [NotNullWhen(true)] out SearchQuery? query,
        [NotNullWhen(false)] out SearchFailure? failure)
    {
        SearchQueryParseResult parsed = post ? SearchQuery.ParseForm(raw) : SearchQuery.Parse("?" + raw);
        query = parsed.Query;
        failure = parsed.Failure;
        return query is not null;
    }

    /// <summary>Accepts only what this host executes faithfully, and builds its shard requests.</summary>
    private bool TryPrepare(bool post, string raw, QueryPlan plan, bool reverse, [NotNullWhen(true)] out PreparedSearch? prepared)
    {
        prepared = null;
        if (plan.PageSize is not { } pageSize || plan.IncludeTotal || plan.Sort is not null)
        {
            return false;
        }

        var revisions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        bool revIncluded = false;
        bool chained = false;
        bool distributed = false;
        int chains = 0;
        foreach (PlannedOccurrence occurrence in plan.Occurrences)
        {
            switch (occurrence.Kind, occurrence.Action)
            {
                case (OccurrenceKind.Control, OccurrenceAction.Remove):
                    break;
                case (OccurrenceKind.RevInclude, OccurrenceAction.Remove):
                    // The caller's grants decide later which sources run; every requested source reference is pinned
                    // regardless, because the planner consulted it for all of them.
                    revIncluded = true;
                    ReferenceHop included = occurrence.Hop!;
                    if (!Pin(included.Path.Source, included.Path.Code))
                    {
                        return false;
                    }

                    break;
                case (OccurrenceKind.Predicate, OccurrenceAction.Keep):
                    if (!Pin(plan.Type, occurrence.Occurrence.Key))
                    {
                        return false;
                    }

                    break;
                case (OccurrenceKind.ReverseChain, OccurrenceAction.Replace):
                    chained = true;
                    ReferenceHop hop = occurrence.Hop!;
                    if (!Pin(hop.Path.Source, hop.Path.Code)
                        || !Pin(hop.Path.Source, WebUtility.UrlDecode(hop.Terminal!.Key))
                        || !Pin(plan.Type, "_id"))
                    {
                        return false;
                    }

                    break;
                case (OccurrenceKind.ForwardChain, OccurrenceAction.Keep):
                    chains++;
                    chained = true;
                    // A passthrough hop has no Terminal, so the terminal definition comes from the planned decoded key: the
                    // text after the dot of the one depth-one chain (the planner admitted no deeper path).
                    ReferenceHop forward = occurrence.Hop!;
                    string key = occurrence.Occurrence.Key;
                    if (reverse
                        || forward.Mode != ChainResolutionMode.Passthrough
                        || !Pin(forward.Path.Source, forward.Path.Code)
                        || !Pin(forward.Path.Target, key[(key.IndexOf('.', StringComparison.Ordinal) + 1)..]))
                    {
                        return false;
                    }

                    break;
                case (OccurrenceKind.ForwardChain, OccurrenceAction.Replace):
                    chains++;
                    chained = true;
                    distributed = true;
                    // The planner consults the source reference and the target's terminal definition, never the target's
                    // _id, so those are the only definitions pinned for this hop.
                    ReferenceHop witnessed = occurrence.Hop!;
                    if (reverse
                        || witnessed.Mode != ChainResolutionMode.Distributed
                        || !Pin(witnessed.Path.Source, witnessed.Path.Code)
                        || !Pin(witnessed.Path.Target, WebUtility.UrlDecode(witnessed.Terminal!.Key)))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        // A reverse include next to any chain would need the chain's own evidence and joins; it is not combined.
        // One distributed forward chain stands alone: it is the only chain in the search.
        if ((revIncluded && chained) || (distributed && (chains != 1 || reverse)))
        {
            return false;
        }

        // A distributed forward chain's streams are the strategy's to produce, as a reverse chain's are the resolver's.
        ShardSearchRequest[] streams = [];
        if (!reverse && !distributed)
        {
            IReadOnlyList<ParameterOccurrence> parameters = plan.BuildBackendParameters(NoReplacements);
            streams = [.. _registry.Shards.Select(s => new ShardSearchRequest(s.Id, plan.Type, parameters, pageSize, SearchMethod(post, s)))];
        }

        prepared = new PreparedSearch(post, raw, streams, pageSize, revisions);
        return true;

        bool Pin(ResourceType type, string key)
        {
            string code = key.Split(':', 2)[0];
            if (_capabilities.GetResolvedDefinitionRevision(type, code) is not { } revision)
            {
                return false;
            }

            revisions[DefinitionKey(type, code)] = revision;
            return true;
        }
    }

    private static ResourceType[] ReverseSources(QueryPlan plan) =>
        [.. plan.Occurrences.Where(o => o.Kind == OccurrenceKind.ReverseChain).Select(o => o.Hop!.Path.Source).Distinct()];

    private static bool IsDistributedForward(QueryPlan plan) =>
        plan.Occurrences.Any(o => (o.Kind, o.Action) is (OccurrenceKind.ForwardChain, OccurrenceAction.Replace));

    /// <summary>The target types the caller must be able to read, regardless of how the planned forward chains are resolved.</summary>
    private static ResourceType[] ForwardTargets(QueryPlan plan) =>
        [.. plan.Occurrences
            .Where(o => o.Kind == OccurrenceKind.ForwardChain)
            .Select(o => o.Hop!.Path.Target)
            .Distinct()];

    private static IResult ForwardDenied() =>
        SearchOutcome.Error(StatusCodes.Status403Forbidden, "forbidden", "The caller may not search a resource type on the forward-chain path.");

    private static IResult ReverseDenied() =>
        SearchOutcome.Error(StatusCodes.Status403Forbidden, "forbidden", "The caller may not search a resource type on the reverse-chain path.");

    private static IResult UnsupportedShape() =>
        SearchOutcome.NotSupported(StatusCodes.Status501NotImplemented, "The search is valid but this host does not yet execute it.");

    private int FetchAllowance => (int)Math.Min((long)_limits.BackendCallsPerShard * _registry.Shards.Count, int.MaxValue);

    private static ShardSearchMethod SearchMethod(bool post, ShardDescriptor shard) =>
        post && shard.Profile.SupportsPostSearch ? ShardSearchMethod.PostSearch : ShardSearchMethod.Get;

    private Uri SelfLink(ResourceType type, string raw) =>
        new($"{_settings.PublicBase}/{type.Value}" + (raw.Length == 0 ? string.Empty : "?" + raw));

    private ContinuationDependencySet Dependencies(PreparedSearch search) =>
        // Even shards without a primary stream contributed to reverse-chain discovery.
        ContinuationDependencySet.Create(_shardRevisions, search.DefinitionRevisions);

    private bool WitnessRegistryGrew(ContinuationDependencySet carried, PreparedSearch search)
    {
        int carriedShardCount = 0;
        bool revisionsCurrent = carried.AreCurrent((group, key) =>
        {
            if (group == ContinuationDependencyGroup.Shard)
            {
                carriedShardCount++;
            }

            return CurrentRevision(group, key);
        });

        // Shard revisions include their ordinal. If they still match, a prior registry with fewer shards can
        // only be a prefix. It must include every saved owner and exactly the same resolved definitions.
        if (!revisionsCurrent || carriedShardCount >= _registry.Shards.Count)
        {
            return false;
        }

        var prefix = _registry.Shards.Take(carriedShardCount)
            .ToDictionary(s => s.Id.Value, s => _shardRevisions[s.Id.Value], StringComparer.Ordinal);
        return search.Streams.All(s => prefix.ContainsKey(s.Shard.Value))
            && SameDependencies(carried, ContinuationDependencySet.Create(prefix, search.DefinitionRevisions));
    }

    /// <summary>Whether two dependency sets are the same canonical set, compared by their canonical encodings.</summary>
    /// <remarks>Lengths are compared first, so the buffers are allocated only at the size of the carried, token-bounded set.</remarks>
    private static bool SameDependencies(ContinuationDependencySet carried, ContinuationDependencySet replanned)
    {
        if (carried.EncodedLength != replanned.EncodedLength)
        {
            return false;
        }

        byte[] expected = new byte[checked((int)carried.EncodedLength)];
        byte[] actual = new byte[expected.Length];
        carried.WriteTo(expected);
        replanned.WriteTo(actual);
        return expected.AsSpan().SequenceEqual(actual);
    }

    /// <summary>The current revision of a dependency an authenticated token names, or <see langword="null"/> when this view lacks it.</summary>
    private string? CurrentRevision(ContinuationDependencyGroup group, string key)
    {
        switch (group)
        {
            case ContinuationDependencyGroup.Shard:
                return _shardRevisions.GetValueOrDefault(key);
            case ContinuationDependencyGroup.Definition:
                int dot = key.IndexOf('.', StringComparison.Ordinal);
                return dot > 0 && ResourceType.TryParse(key[..dot], out ResourceType? type)
                    ? _capabilities.GetResolvedDefinitionRevision(type, key[(dot + 1)..])
                    : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// An accepted search: its origin, stored raw query, shard requests, and the revision of each definition its
    /// predicates resolved to, keyed <c>{type}.{code}</c>.
    /// </summary>
    private sealed record PreparedSearch(
        bool Post,
        string RawQuery,
        ShardSearchRequest[] Streams,
        int PageSize,
        IReadOnlyDictionary<string, string> DefinitionRevisions);

    /// <summary>Exact equality of shard requests, which do not define equality themselves.</summary>
    private sealed class SameRequest : IEqualityComparer<ShardSearchRequest>
    {
        public static readonly SameRequest Instance = new();

        public bool Equals(ShardSearchRequest? x, ShardSearchRequest? y) =>
            x is not null && y is not null
            && x.Shard == y.Shard
            && x.Type == y.Type
            && x.Method == y.Method
            && x.PageSize == y.PageSize
            && x.Parameters.SequenceEqual(y.Parameters);

        public int GetHashCode(ShardSearchRequest obj) => HashCode.Combine(obj.Shard, obj.Type, obj.Method, obj.PageSize);
    }
}
