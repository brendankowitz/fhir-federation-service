using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Ignixa.Federation.Api.Authentication;
using Ignixa.Federation.Api.Authorization;
using Ignixa.Federation.Api.Budgets;
using Ignixa.Federation.Api.Continuations;
using Ignixa.Federation.Api.Responses;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Ignixa.Federation.Api.Search;

/// <summary>
/// The public primary search: <c>GET [type]</c>, <c>POST [type]/_search</c> and <c>GET [type]?ct=…</c>, one
/// authenticated path from caller to shards and back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Only plain predicates over every shard, unsorted, with a page size: the planner's admitted
/// <c>Predicate/Keep</c> and <c>Control/Remove</c> occurrences. Anything else the planner admits (chains, includes,
/// sort, counts, totals) is refused with 501 before any shard call, not approximated.
/// </para>
/// <para>
/// <b>Continuations.</b> A token carries the stored original raw query and the executor's snapshot. Redemption
/// replans that query on this host's view and requires exactly the saved shard requests and page size; expiry,
/// relevant registry or plan drift, and a missing anchor are 410, and authentic bytes this host cannot read are 400,
/// all before any shard call. Definition dependencies are pinned from the discovery projection
/// (<see cref="SearchCapabilities.Resources"/>), which omits some parameters the planner admits (for example
/// reference <c>:missing</c>); a predicate the projection cannot pin is refused with 501 rather than issued
/// unpinned. A next link that does not fit inline is 503, never a truncated or partial page.
/// </para>
/// <para>
/// <b>Failure.</b> One <see cref="RequestBudget"/> spans the request from entry (body read, planning, shard work and
/// materializing the page). Every failure is a whole-request <c>OperationOutcome</c> with fixed text that names no
/// query value, token or backend detail; a failing shard is named only by its alias. Caller cancellation propagates.
/// </para>
/// <para>Thread-safe: every field is immutable after construction; per-request state lives on the stack.</para>
/// </remarks>
internal sealed class PrimarySearch
{
    private const string Scheme = FederationCallerAuthentication.SchemeName;
    private const int MaxRawQueryLength = 8 * 1024;
    private const int MaxBodyBytes = 32 * 1024;
    private const string FormMediaType = "application/x-www-form-urlencoded";

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
    private readonly FrozenDictionary<string, string> _definitionRevisions;
    private readonly FrozenDictionary<string, string> _shardRevisions;

    private PrimarySearch(
        SearchHostSettings settings,
        SearchCapabilities capabilities,
        FederationOptions options,
        IShardClientFactory clients,
        IDataProtectionProvider protection,
        TimeProvider time)
    {
        _settings = settings;
        _capabilities = capabilities;
        _registry = capabilities.Registry;
        _limits = options.Limits;
        _timeouts = options.Timeouts;
        _planner = new SearchPlanner(capabilities, options.ChainResolution, options.Limits);
        _protector = new ContinuationProtector(protection, options.Tokens, time);
        _codec = new UnsortedSnapshotCodec(options.Tokens.MaxInlineTokenBytes, _registry);
        _clients = clients;
        _time = time;

        // The definition revision is the projection's identity of the parameter (type and canonical definition), the
        // closest thing to a definition version this view exposes; a host whose view no longer publishes it, or
        // publishes a different definition, refuses the token.
        _definitionRevisions = capabilities.Resources
            .SelectMany(r => r.Parameters.Select(p => KeyValuePair.Create(DefinitionKey(r.Type, p.Code), $"{p.Type}|{p.Definition}")))
            .ToFrozenDictionary(StringComparer.Ordinal);
        _shardRevisions = _registry.Shards
            .ToFrozenDictionary(s => s.Id.Value, s => _registry.ComputeVersion([s.Id]), StringComparer.Ordinal);
    }

    /// <summary>Builds the search from the registered view, transport and the <c>Federation</c> configuration.</summary>
    /// <param name="services">The application services.</param>
    /// <param name="settings">The validated host settings.</param>
    /// <returns>The search.</returns>
    /// <exception cref="InvalidOperationException">
    /// No view or transport is registered, the <c>Federation</c> options are invalid, or its shards do not describe
    /// exactly the view's registry. Messages carry only configuration paths.
    /// </exception>
    public static PrimarySearch Create(IServiceProvider services, SearchHostSettings settings)
    {
        SearchCapabilities capabilities = services.GetService<SearchCapabilities>()
            ?? throw new InvalidOperationException("Search is configured but no SearchCapabilities view is registered.");
        IShardClientFactory clients = services.GetService<IShardClientFactory>()
            ?? throw new InvalidOperationException("Search is configured but no shard transport (IShardClientFactory) is registered.");

        var options = new FederationOptions();
        services.GetRequiredService<IConfiguration>().GetSection(FederationOptions.SectionName).Bind(options);
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
            services.GetRequiredService<IDataProtectionProvider>(),
            services.GetRequiredService<TimeProvider>());
    }

    /// <summary>Serves one search request.</summary>
    /// <param name="http">The request.</param>
    /// <param name="route">The route's resource type, already accepted by <see cref="ResourceTypeRouteConstraint"/>.</param>
    /// <param name="post">Whether the request is <c>POST [type]/_search</c>.</param>
    /// <returns>The searchset page or the refusal.</returns>
    /// <exception cref="OperationCanceledException">The caller aborted the request.</exception>
    public async Task<IResult> SearchAsync(HttpContext http, string route, bool post)
    {
        ResourceType type = ResourceType.Parse(route);

        // One budget from entry: the deadline bounds the body read and planning as well as the shard work, and its
        // classification (caller cancellation, deadline, cost) is the one boundary every failure passes through.
        using var budget = new RequestBudget(_limits, _timeouts, _registry.Shards.Count, _time, http.RequestAborted);
        try
        {
            try
            {
                return await ServeAsync(http, type, post, budget);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ShardFailureException)
            {
                // Possibly the symptom of the request's own deadline or cost: report the cause. Caller cancellation
                // propagates as OperationCanceledException.
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
            return SearchOutcome.Error(StatusCodes.Status504GatewayTimeout, "timeout", "The search did not complete in time.");
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
    }

    private async Task<IResult> ServeAsync(HttpContext http, ResourceType type, bool post, RequestBudget budget)
    {
        // Authenticate and authorise before reading any body or query semantics.
        AuthenticateResult authentication = await http.AuthenticateAsync(Scheme);
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

        if (!TryPlan(type, post, raw, out SearchPlanResult? planned, out SearchFailure? failure))
        {
            return failure.Code == SearchFailureCode.UnsupportedFormat
                ? SearchOutcome.NotSupported(StatusCodes.Status415UnsupportedMediaType, failure.Message)
                : failure.IsNotSupported
                    ? SearchOutcome.NotSupported(StatusCodes.Status400BadRequest, failure.Message)
                    : SearchOutcome.Invalid(failure.Message);
        }

        if (planned.Continuation is { } continuation)
        {
            return post
                ? SearchOutcome.Invalid("A continuation is followed with GET, not POST.")
                : await ContinueAsync(type, binding, continuation.Token, budget);
        }

        if (!TryPrepare(type, post, raw, planned.Plan!, out PreparedSearch? prepared))
        {
            return SearchOutcome.NotSupported(StatusCodes.Status501NotImplemented, "The search is valid but this host does not yet execute it.");
        }

        return await ExecuteAsync(type, binding, prepared, predecessor: null, resumeFrom: null, budget);
    }

    private async Task<IResult> ContinueAsync(ResourceType type, ContinuationBinding binding, string token, RequestBudget budget)
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

        // Replan the stored query on this host's view: it must admit exactly the saved shard requests.
        if (!TryPlan(type, frame.Post, frame.RawQuery, out SearchPlanResult? planned, out _)
            || planned.Plan is not { } plan
            || !TryPrepare(type, frame.Post, frame.RawQuery, plan, out PreparedSearch? prepared)
            || prepared.PageSize != snapshot.PageSize
            || !prepared.Streams.SequenceEqual(snapshot.Streams, SameRequest.Instance))
        {
            return SearchOutcome.Gone();
        }

        return await ExecuteAsync(type, binding, prepared, predecessor, snapshot.Continuation, budget);
    }

    /// <summary>Runs the shards for one page, links its successor, and materializes the response within the deadline.</summary>
    /// <remarks>Budget, deadline and shard failures propagate to <see cref="SearchAsync"/>, which classifies them.</remarks>
    private async Task<IResult> ExecuteAsync(
        ResourceType type,
        ContinuationBinding binding,
        PreparedSearch search,
        RedeemedContinuation? predecessor,
        UnsortedContinuation? resumeFrom,
        RequestBudget budget)
    {
        // A request whose deadline passed while its body was read or planned never reaches a shard.
        budget.Checkpoint();
        var executor = new UnsortedPageExecutor(_clients.CreateClient(_capabilities, budget), _registry, _limits.MaxParallelBackendCalls);
        int fetchBudget = (int)Math.Min((long)_limits.BackendCallsPerShard * _registry.Shards.Count, int.MaxValue);
        UnsortedPage page = await executor.ExecuteAsync(search.Streams, search.PageSize, resumeFrom, fetchBudget, budget.Token);

        // A page that exceeded the cost budget is too costly, not a page with an unlinkable successor.
        budget.Checkpoint();
        Uri? next = null;
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
                ? _protector.Issue(binding, Dependencies(type, search), frame)
                : _protector.IssueSuccessor(predecessor, frame);
            if (issue.InlineToken is not { } token)
            {
                return NextTooLarge();
            }

            next = new Uri($"{_settings.PublicBase}/{type.Value}?ct={token}");
        }

        var self = new Uri($"{_settings.PublicBase}/{type.Value}" + (search.RawQuery.Length == 0 ? string.Empty : "?" + search.RawQuery));
        IResult response;
        try
        {
            response = SearchsetResponse.Create(_registry, self, page.Matches, [], [], next);
        }
        catch (ArgumentException)
        {
            return SearchOutcome.Error(StatusCodes.Status502BadGateway, "exception", "A shard returned a resource the federation cannot serve.");
        }

        // The page's bytes are materialized; it is served only if that finished within the deadline.
        budget.Checkpoint();
        return response;
    }

    private static IResult NextTooLarge() =>
        SearchOutcome.Unavailable("The next page cannot be linked: its continuation exceeds the inline token limit.");

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

    private bool TryPlan(
        ResourceType type,
        bool post,
        string raw,
        [NotNullWhen(true)] out SearchPlanResult? planned,
        [NotNullWhen(false)] out SearchFailure? failure)
    {
        // The same rule parses a search when it arrives and when its stored query is replanned.
        SearchQueryParseResult parsed = post ? SearchQuery.ParseForm(raw) : SearchQuery.Parse("?" + raw);
        if (parsed.Query is null)
        {
            planned = null;
            failure = parsed.Failure!;
            return false;
        }

        SearchPlanResult result = _planner.Plan(type, parsed.Query);
        planned = result.Failure is null ? result : null;
        failure = result.Failure;
        return failure is null;
    }

    /// <summary>Accepts only what this host executes faithfully, and builds its shard requests.</summary>
    private bool TryPrepare(ResourceType type, bool post, string raw, QueryPlan plan, [NotNullWhen(true)] out PreparedSearch? prepared)
    {
        prepared = null;
        if (plan.PageSize is not { } pageSize || plan.IncludeTotal || plan.Sort is not null)
        {
            return false;
        }

        var codes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (PlannedOccurrence occurrence in plan.Occurrences)
        {
            switch (occurrence.Kind, occurrence.Action)
            {
                case (OccurrenceKind.Control, OccurrenceAction.Remove):
                    break;
                case (OccurrenceKind.Predicate, OccurrenceAction.Keep):
                    // A predicate the projection cannot pin to a definition could not be checked on redemption.
                    string code = occurrence.Occurrence.Key.Split(':', 2)[0];
                    if (!_definitionRevisions.ContainsKey(DefinitionKey(type, code)))
                    {
                        return false;
                    }

                    codes.Add(code);
                    break;
                default:
                    return false;
            }
        }

        IReadOnlyList<ParameterOccurrence> parameters = plan.BuildBackendParameters(NoReplacements);
        ShardSearchRequest[] streams =
        [
            .. _registry.Shards.Select(s => new ShardSearchRequest(
                s.Id,
                type,
                parameters,
                pageSize,
                post && s.Profile.SupportsPostSearch ? ShardSearchMethod.PostSearch : ShardSearchMethod.Get)),
        ];
        prepared = new PreparedSearch(post, raw, streams, pageSize, [.. codes]);
        return true;
    }

    private ContinuationDependencySet Dependencies(ResourceType type, PreparedSearch search) =>
        ContinuationDependencySet.Create(
            search.Streams.Select(s => KeyValuePair.Create(s.Shard.Value, _shardRevisions[s.Shard.Value])),
            search.DefinitionCodes.Select(code =>
            {
                string key = DefinitionKey(type, code);
                return KeyValuePair.Create(key, _definitionRevisions[key]);
            }));

    private string? CurrentRevision(ContinuationDependencyGroup group, string key) => group switch
    {
        ContinuationDependencyGroup.Shard => _shardRevisions.GetValueOrDefault(key),
        ContinuationDependencyGroup.Definition => _definitionRevisions.GetValueOrDefault(key),
        _ => null,
    };

    /// <summary>An accepted search: its origin, stored raw query, shard requests, and the definitions it depends on.</summary>
    private sealed record PreparedSearch(bool Post, string RawQuery, ShardSearchRequest[] Streams, int PageSize, string[] DefinitionCodes);

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
