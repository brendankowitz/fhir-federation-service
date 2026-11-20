using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Capabilities;
using Ignixa.Federation.Shards.Diagnostics;

namespace Ignixa.Federation.Shards.Http;

/// <summary>Searches and reads one shard over HTTPS, one backend exchange per page or read (design spec §5.1 and §7).</summary>
/// <remarks>
/// <para><b>Wire.</b> Search parameters are sent verbatim in planner order, then one <c>_count</c>: as the query of
/// <c>GET {base}/{type}</c>, or as the <c>application/x-www-form-urlencoded</c> body of <c>POST {base}/{type}/_search</c>.
/// Every exchange carries <c>Prefer: handling=strict</c>, <c>Accept: application/fhir+json</c> and the bearer token, and
/// nothing else of the caller's. Redirects, cookies and compression are off.</para>
/// <para><b>Self check.</b> A first page must have exactly one <c>self</c> link whose query decodes to the same pairs as
/// the query sent, duplicates counted, in any order; otherwise the backend answered a different search and the call fails
/// with <see cref="ShardCallFailure.SelfLinkMismatch"/>. No backend-owned parameter is exempt, because none is documented.</para>
/// <para><b>Continuation.</b> A <c>next</c> link is accepted only as a continuation of this shard's own typed search (the
/// configured base or a configured link base alias), is kept verbatim in the <see cref="ShardCursor"/>, and is redeemed
/// by sending only its query to the configured base, by GET or by POST to <c>_search</c> as the cursor says. A link that
/// would immediately repeat the query just sent is refused; detecting a longer cycle across pages is the caller's
/// responsibility (design spec §7.8).</para>
/// <para><b>Self link.</b> The first page's <c>self</c> must be a usable URI reference (RFC 3986) without a fragment. Its
/// path is not checked, because it is never requested; links are normalized to the configured base (design spec §4.2).</para>
/// <para><b>Limits.</b> Each call, from credential acquisition to the last body byte, has the deadline and response size
/// limit given at construction; a credential provider that ignores cancellation cannot extend the deadline. The optional
/// <see cref="IShardTransferMeter"/> sees each logical send attempt and every body byte the client consumes, which is at
/// most one byte past the limit, so a caller can enforce an aggregate budget. A rejected response's body is never
/// drained to reuse its connection; the connection is closed instead.</para>
/// <para><b>Tracing.</b> The ambient W3C trace context is propagated, but never baggage (<c>baggage</c> or
/// <c>Correlation-Context</c>): baggage is arbitrary application data and a shard is a separate trust domain.</para>
/// <para><b>Failures.</b> A call either returns a page that represents the backend's answer exactly or throws: a
/// classified <see cref="ShardCallException"/>, an <see cref="OperationCanceledException"/> when the caller cancels, or
/// the credential provider's or meter's own exception. It never returns an empty page for a failure, and never retries.</para>
/// <para><b>Read.</b> <see cref="ReadAsync"/> is the same bounded exchange for one resource, <c>GET {base}/{type}/{id}</c>,
/// with its own <c>read</c> span. Only there are HTTP 404 and 410 classified as the resource's absence.</para>
/// <para><b>Capabilities.</b> <see cref="GetCapabilitiesAsync"/> is the same bounded exchange for <c>GET {base}/metadata</c>,
/// with its own <c>metadata</c> span; there 404 and 410 are unexpected statuses, because the advertisement is not a resource.</para>
/// <para>Thread-safe: concurrent calls are independent.</para>
/// </remarks>
public sealed class HttpShardClient : IShardClient, IDisposable
{
    private const int ChunkBytes = 16 * 1024;
    private const string Stage = "search";
    private const string ReadStage = "read";
    private const string MetadataStage = "metadata";

    // What one exchange asks of the backend; it decides how a status is read and what a failure says.
    private enum Operation
    {
        Search,
        Read,
        Metadata,
    }

    // CancellationTokenSource.CancelAfter accepts at most uint.MaxValue - 1 milliseconds.
    private static readonly TimeSpan MaxCallTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private static readonly UriCreationOptions Verbatim = new() { DangerousDisablePathAndQueryCanonicalization = true };

    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };

    private readonly ShardDescriptor _shard;
    private readonly IShardCredentialProvider _credentials;
    private readonly TimeSpan _callTimeout;
    private readonly long _maxResponseBytes;
    private readonly IShardTransferMeter? _meter;
    private readonly LinkResolver _links;
    private readonly HttpMessageInvoker _invoker;

    /// <summary>Creates a client for one shard, trusting the platform's certificate store.</summary>
    /// <param name="shard">The shard every call goes to.</param>
    /// <param name="credentials">Acquires the bearer token for <see cref="ShardDescriptor.Auth"/>, once per call.</param>
    /// <param name="callTimeout">The deadline for one whole call, from credential acquisition to the last body byte.</param>
    /// <param name="maxResponseBytes">The largest response body one call reads.</param>
    /// <param name="meter">Observes actual sends and response bytes, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shard"/> or <paramref name="credentials"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="callTimeout"/> is not positive or too long for a deadline, or <paramref name="maxResponseBytes"/> is
    /// not positive or exceeds <see cref="Array.MaxLength"/>.
    /// </exception>
    public HttpShardClient(
        ShardDescriptor shard,
        IShardCredentialProvider credentials,
        TimeSpan callTimeout,
        long maxResponseBytes,
        IShardTransferMeter? meter = null)
        : this(shard, credentials, callTimeout, maxResponseBytes, meter, trustedTestCertificate: null)
    {
    }

    /// <param name="shard">The shard every call goes to.</param>
    /// <param name="credentials">Acquires the bearer token, once per call.</param>
    /// <param name="callTimeout">The deadline for one whole call.</param>
    /// <param name="maxResponseBytes">The largest response body one call reads.</param>
    /// <param name="meter">Observes actual sends and response bytes, or <see langword="null"/>.</param>
    /// <param name="trustedTestCertificate">
    /// When set, the only certificate trusted for the TLS server, in place of the platform store; tests use it for a
    /// loopback server. Revocation is not checked for it.
    /// </param>
    internal HttpShardClient(
        ShardDescriptor shard,
        IShardCredentialProvider credentials,
        TimeSpan callTimeout,
        long maxResponseBytes,
        IShardTransferMeter? meter,
        X509Certificate2? trustedTestCertificate)
    {
        ArgumentNullException.ThrowIfNull(shard);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(callTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(callTimeout, MaxCallTimeout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResponseBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxResponseBytes, Array.MaxLength);

        _shard = shard;
        _credentials = credentials;
        _callTimeout = callTimeout;
        _maxResponseBytes = maxResponseBytes;
        _meter = meter;
        _links = new LinkResolver(shard);
        _invoker = CreateInvoker(trustedTestCertificate);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The request is for another shard, asks for POST search on a backend without it, or has a parameter that cannot be
    /// sent verbatim (not visible ASCII, a <c>#</c>, or a <c>%</c> that does not start an escape). Nothing is sent.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The page size exceeds the backend's maximum <c>_count</c>. Nothing is sent.</exception>
    /// <exception cref="ShardCallException">The exchange failed or the response was not an exact answer to the request.</exception>
    public Task<ShardPage> SearchAsync(ShardSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Shard != _shard.Id)
        {
            throw new ArgumentException("The request is for another shard.", nameof(request));
        }

        bool post = request.Method == ShardSearchMethod.PostSearch;
        if (post && !_shard.Profile.SupportsPostSearch)
        {
            throw new ArgumentException("The shard's backend does not support POST search.", nameof(request));
        }

        if (request.PageSize > _shard.Profile.MaxCount)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.PageSize, "The page size exceeds the backend's maximum _count.");
        }

        string query = BuildQuery(request);
        return CallAsync(request.Type, query, post, page => FirstPage(request, query, page), cancellationToken);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="cursor"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The cursor is for another shard, its target is not a continuation of this shard's search, or it reposts to a
    /// backend without POST search. Nothing is sent.
    /// </exception>
    /// <exception cref="ShardCallException">The exchange failed or the response was not a readable page.</exception>
    public Task<ShardPage> ContinueAsync(ShardCursor cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        if (cursor.Shard != _shard.Id)
        {
            throw new ArgumentException("The cursor is for another shard.", nameof(cursor));
        }

        string query = _links.QueryOf(cursor.Type, cursor.Target)
            ?? throw new ArgumentException("The cursor target is not a continuation of this shard's search.", nameof(cursor));
        bool post = cursor.Redemption == NextLinkRedemption.RepostToSearch;
        if (post && !_shard.Profile.SupportsPostSearch)
        {
            throw new ArgumentException("The shard's backend does not support POST search.", nameof(cursor));
        }

        return CallAsync(
            cursor.Type,
            query,
            post,
            page => new ShardPage(
                page.Entries,
                NextCursor(page, cursor.Type, query, link => new ShardCursor(cursor.Shard, cursor.Type, link, cursor.Redemption)),
                page.Total),
            cancellationToken);
    }

    /// <summary>Releases the connection pool. The client must not be used afterwards.</summary>
    public void Dispose() => _invoker.Dispose();

    /// <summary>
    /// Fetches this shard's advertisement: a single <c>GET {base}/metadata</c> with the same credential, deadline, size
    /// limit, metering and no-redirect rules as a search, with its own <c>metadata</c> span.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// What the shard's R4 CapabilityStatement advertises, as read by <see cref="CapabilityStatementReader"/>. Advertised
    /// is not certified: nothing is compared with the shard's profile or any other shard.
    /// </returns>
    /// <exception cref="ShardCallException">
    /// The exchange failed, or the body is not valid UTF-8 or not an R4 CapabilityStatement the reader accepts
    /// (<see cref="ShardCallFailure.MalformedResponse"/>). Any status other than 200, including 404 and 410, is a failure
    /// of the advertisement, not the absence of a resource; no error body is read, and nothing is retried.
    /// </exception>
    public async Task<ShardCapabilityEvidence> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Activity? activity = ShardActivity.StartBackendCall(_shard.Alias.Value, MetadataStage);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, new Uri($"{_shard.CanonicalBaseUrl}/metadata", in Verbatim));

            // The reader runs inside the exchange, so it is under the same deadline and caller cancellation.
            return await ExchangeAsync(message, Operation.Metadata, ReadCapabilities, cancellationToken).ConfigureAwait(false);
        }
        catch (ShardCallException e)
        {
            activity?.SetStatus(ActivityStatusCode.Error, e.Failure.ToString());
            throw;
        }
    }

    private ShardCapabilityEvidence ReadCapabilities(ReadOnlyMemory<byte> body)
    {
        // The reader trusts the JSON parser's view of the bytes; an ignored property must not hide invalid text.
        if (!Utf8.IsValid(body.Span))
        {
            throw Failure(ShardCallFailure.MalformedResponse, "The body is not valid UTF-8.");
        }

        try
        {
            return CapabilityStatementReader.Read(body);
        }
        catch (FormatException)
        {
            // The reader's message is safe to log, but a fixed diagnostic keeps every failure of this kind identical.
            throw Failure(ShardCallFailure.MalformedResponse, "The body is not an R4 CapabilityStatement that advertises.");
        }
    }

    /// <summary>
    /// Reads the current version of one resource: a single <c>GET {base}/{type}/{id}</c> at the key's own shard, with the
    /// same credential, deadline, size limit, metering and no-redirect rules as a search.
    /// </summary>
    /// <param name="key">The resource to read; its shard must be this client's.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The response body exactly as received, and the URL it was read from, built from the registered base and the key
    /// alone. The body is only returned when it is one JSON object whose <c>resourceType</c> and <c>id</c> are the key's;
    /// that is identity framing, not FHIR schema validation.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The key is for another shard, or its id is <c>.</c> or <c>..</c>, which as a URL path segment would address
    /// something other than the resource. Nothing is sent and no credential is acquired.
    /// </exception>
    /// <exception cref="ShardCallException">
    /// The exchange failed. HTTP 404 and 410 are <see cref="ShardCallFailure.ResourceNotFound"/> and
    /// <see cref="ShardCallFailure.ResourceGone"/> here only; no error body is read, and nothing is retried or fallen back to.
    /// </exception>
    public Task<ShardReadResult> ReadAsync(ResourceKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Shard != _shard.Id)
        {
            throw new ArgumentException("The key is for another shard.", nameof(key));
        }

        // A lexical R4 id, but as a path segment it is a dot segment that a URL resolver may fold into the parent path.
        if (key.Id.Value is "." or "..")
        {
            throw new ArgumentException("The id cannot be addressed as a URL path segment.", nameof(key));
        }

        return ReadCoreAsync(key, cancellationToken);
    }

    private async Task<ShardReadResult> ReadCoreAsync(ResourceKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Activity? activity = ShardActivity.StartBackendCall(_shard.Alias.Value, ReadStage);
        try
        {
            var location = new Uri($"{_shard.CanonicalBaseUrl}/{key.Type.Value}/{key.Id.Value}", in Verbatim);
            using var message = new HttpRequestMessage(HttpMethod.Get, location);

            // The identity check runs inside the exchange, so it is under the same deadline and caller cancellation.
            return await ExchangeAsync(message, Operation.Read, body => ReadResource(body, location, key), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ShardCallException e)
        {
            activity?.SetStatus(ActivityStatusCode.Error, e.Failure.ToString());
            throw;
        }
    }

    private ShardReadResult ReadResource(ReadOnlyMemory<byte> body, Uri location, ResourceKey key)
    {
        // The read keeps the bytes as received, so they must be text a downstream consumer decodes the same way.
        if (!Utf8.IsValid(body.Span))
        {
            throw Failure(ShardCallFailure.MalformedResponse, "The body is not valid UTF-8.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body, StrictJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !Searchset.IsString(root, "resourceType", key.Type.Value)
                || !Searchset.IsString(root, "id", key.Id.Value))
            {
                throw Failure(ShardCallFailure.MalformedResponse, "The body is not the requested resource.");
            }
        }
        catch (JsonException)
        {
            throw Failure(ShardCallFailure.MalformedResponse, "The body is not valid JSON.");
        }

        return new ShardReadResult(location, body);
    }

    // Redirects, cookies, decompression and ambient credentials are off: the client sends exactly what it builds and
    // reads exactly what the backend returns. A response left unread (a failed status, an oversized body) is never
    // drained for connection reuse, because those bytes would be neither metered nor bounded by the call; the connection
    // is closed instead. Baggage is withheld from the propagated trace context.
    private static HttpMessageInvoker CreateInvoker(X509Certificate2? trustedTestCertificate)
    {
        SocketsHttpHandler? handler = null;
        try
        {
            handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
                PreAuthenticate = false,
                Credentials = null,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxResponseDrainSize = 0,
                ActivityHeadersPropagator = new TraceContextOnlyPropagator(DistributedContextPropagator.Current),
            };
            if (trustedTestCertificate is not null)
            {
                handler.SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = TrustOnly(trustedTestCertificate) };
            }

            var invoker = new HttpMessageInvoker(handler, disposeHandler: true);
            handler = null;
            return invoker;
        }
        finally
        {
            handler?.Dispose();
        }
    }

    private static X509ChainPolicy TrustOnly(X509Certificate2 certificate)
    {
        var chain = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        chain.CustomTrustStore.Add(certificate);
        return chain;
    }

    private static string BuildQuery(ShardSearchRequest request)
    {
        var query = new StringBuilder();
        foreach (ParameterOccurrence parameter in request.Parameters)
        {
            if (!QueryText.IsWireSafe(parameter.Key) || !QueryText.IsWireSafe(parameter.Value))
            {
                // The text is deliberately not quoted: search values may be PHI.
                throw new ArgumentException(
                    "A search parameter cannot be sent verbatim: it must be visible ASCII other than '#', with every '%' starting an escape.",
                    nameof(request));
            }

            query.Append(parameter.Key).Append('=').Append(parameter.Value).Append('&');
        }

        return query.Append("_count=").Append(request.PageSize.ToString(CultureInfo.InvariantCulture)).ToString();
    }

    // RFC 6750 b64token: the only text a bearer credential may be.
    private static bool IsBearerToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        ReadOnlySpan<char> body = token.AsSpan().TrimEnd('=');
        if (body.IsEmpty)
        {
            return false;
        }

        foreach (char c in body)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_' or '~' or '+' or '/'))
            {
                return false;
            }
        }

        return true;
    }

    private ShardPage FirstPage(ShardSearchRequest request, string sent, Searchset page)
    {
        string self = page.Self ?? throw Failure(ShardCallFailure.MalformedResponse, "The first page has no self link.");
        int questionMark = self.IndexOf('?', StringComparison.Ordinal);
        if (!IsUriReference(self, questionMark < 0 ? self.Length : questionMark))
        {
            throw Failure(ShardCallFailure.MalformedResponse, "The self link is not a URI reference without a fragment.");
        }

        if (questionMark < 0)
        {
            throw Failure(ShardCallFailure.SelfLinkMismatch, "The self link does not report the search that was sent.");
        }

        if (!QueryText.TryDecodePairs(self.AsSpan(questionMark + 1), out List<(string Key, string Value)> reported))
        {
            throw Failure(ShardCallFailure.MalformedResponse, "The self link query cannot be decoded.");
        }

        if (!QueryText.TryDecodePairs(sent, out List<(string Key, string Value)> sentPairs) || !QueryText.SameMultiset(reported, sentPairs))
        {
            // The parameter is deliberately not named: search values may be PHI.
            throw Failure(ShardCallFailure.SelfLinkMismatch, "The self link does not report the search that was sent.");
        }

        if (request.PageSize == 0 && (page.Total is null || page.Entries.Count > 0 || page.Next is not null))
        {
            throw Failure(ShardCallFailure.MalformedResponse, "A count-only page must have a total and no entries or next link.");
        }

        return new ShardPage(page.Entries, NextCursor(page, request.Type, sent, link => ShardCursor.Next(request, _shard.Profile, link)), page.Total);
    }

    // RFC 3986 URI-reference, checked syntactically: the whole text is visible ASCII with valid escapes and no fragment,
    // the part before the query holds only URI characters, a leading scheme is well formed, and the platform can resolve
    // it against the configured base, which validates any authority and port. The query is compared separately and stays
    // permissive, because backends echo characters such as '|' unescaped.
    private bool IsUriReference(string self, int queryStart)
    {
        if (!QueryText.IsWireSafe(self))
        {
            return false;
        }

        ReadOnlySpan<char> beforeQuery = self.AsSpan(0, queryStart);
        foreach (char c in beforeQuery)
        {
            if (!char.IsAsciiLetterOrDigit(c) && !"-._~!$&'()*+,;=:@/%[]".Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        int colon = beforeQuery.IndexOf(':');
        int slash = beforeQuery.IndexOf('/');
        if (colon >= 0 && (slash < 0 || colon < slash) && !IsScheme(beforeQuery[..colon]))
        {
            return false;
        }

        return Uri.TryCreate(_shard.BaseUrl, self, out _);
    }

    private static bool IsScheme(ReadOnlySpan<char> scheme)
    {
        if (scheme.IsEmpty || !char.IsAsciiLetter(scheme[0]))
        {
            return false;
        }

        foreach (char c in scheme[1..])
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private ShardCursor? NextCursor(Searchset page, ResourceType type, string sent, Func<string, ShardCursor> cursor)
    {
        if (page.Next is not { } link)
        {
            return null;
        }

        string? query = _links.QueryOf(type, link);
        if (query is null)
        {
            throw Failure(ShardCallFailure.InvalidNextLink, "The next link is not a continuation of this shard's search.");
        }

        if (QueryText.SamePairs(query, sent))
        {
            throw Failure(ShardCallFailure.InvalidNextLink, "The next link repeats the page just fetched.");
        }

        return cursor(link);
    }

    private async Task<ShardPage> CallAsync(
        ResourceType type,
        string query,
        bool post,
        Func<Searchset, ShardPage> interpret,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Activity? activity = ShardActivity.StartBackendCall(_shard.Alias.Value, Stage);
        try
        {
            using HttpRequestMessage message = post
                ? new HttpRequestMessage(HttpMethod.Post, new Uri($"{_shard.CanonicalBaseUrl}/{type.Value}/_search", in Verbatim))
                {
                    Content = FormBody(query),
                }
                : new HttpRequestMessage(HttpMethod.Get, new Uri($"{_shard.CanonicalBaseUrl}/{type.Value}?{query}", in Verbatim));
            ReadOnlyMemory<byte> body = await ExchangeAsync(message, Operation.Search, static body => body, cancellationToken)
                .ConfigureAwait(false);
            ShardPage page = interpret(Searchset.Read(body, _shard, type));
            cancellationToken.ThrowIfCancellationRequested();
            return page;
        }
        catch (ShardCallException e)
        {
            activity?.SetStatus(ActivityStatusCode.Error, e.Failure.ToString());
            throw;
        }
    }

    private static ByteArrayContent FormBody(string query)
    {
        var content = new ByteArrayContent(Encoding.ASCII.GetBytes(query));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        return content;
    }

    // Runs one bounded exchange and hands the whole body to interpret, still under the deadline and the caller's token.
    private async Task<T> ExchangeAsync<T>(
        HttpRequestMessage message,
        Operation operation,
        Func<ReadOnlyMemory<byte>, T> interpret,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_callTimeout);
        try
        {
            // WaitAsync bounds a provider that ignores its token; the abandoned acquisition is left to finish on its own.
            string token = await _credentials.GetAccessTokenAsync(_shard.Auth, deadline.Token).AsTask()
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            if (!IsBearerToken(token))
            {
                // The token is deliberately not quoted.
                throw new InvalidOperationException("The credential provider returned text that is not a bearer token.");
            }

            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
            message.Headers.TryAddWithoutValidation("Prefer", "handling=strict");

            // A token that arrived after the deadline, or after the caller gave up, is not used.
            deadline.Token.ThrowIfCancellationRequested();
            _meter?.RecordSend(_shard.Id);
            HttpResponseMessage response = await OnWireAsync(
                () => new ValueTask<HttpResponseMessage>(_invoker.SendAsync(message, deadline.Token)), deadline, cancellationToken)
                .ConfigureAwait(false);

            using (response)
            {
                CheckStatus(response, operation);
                ReadOnlyMemory<byte> body = await ReadBodyAsync(response.Content, deadline, cancellationToken).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                T result = interpret(body);
                deadline.Token.ThrowIfCancellationRequested();
                return result;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Rethrown for the caller's token, whichever token the canceled operation observed.
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw Failure(ShardCallFailure.Timeout, "The call did not complete within its deadline.");
        }
    }

    // Only a qualified read treats 404 and 410 as the resource's absence; a search or metadata fetch that gets them is
    // still an unexpected status.
    private void CheckStatus(HttpResponseMessage response, Operation operation)
    {
        bool qualifiedRead = operation == Operation.Read;
        int status = (int)response.StatusCode;
        ShardCallFailure? failure = status switch
        {
            200 => null,
            404 when qualifiedRead => ShardCallFailure.ResourceNotFound,
            410 when qualifiedRead => ShardCallFailure.ResourceGone,
            >= 300 and < 400 => ShardCallFailure.Redirect,
            401 or 403 => ShardCallFailure.Authentication,
            _ => ShardCallFailure.UnexpectedStatus,
        };

        // The body of a failed exchange is never read: it may echo the query or hold backend detail.
        if (failure is { } classified)
        {
            throw Failure(
                classified,
                operation switch
                {
                    Operation.Read => "The backend did not return the resource.",
                    Operation.Metadata => "The backend did not return its capability statement.",
                    _ => "The backend did not answer with a search page.",
                },
                status);
        }
    }

    private async Task<ReadOnlyMemory<byte>> ReadBodyAsync(HttpContent content, CancellationTokenSource deadline, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > _maxResponseBytes)
        {
            throw Failure(ShardCallFailure.ResponseTooLarge, "The response body exceeds the size limit.");
        }

        Stream stream = await OnWireAsync(
            () => new ValueTask<Stream>(content.ReadAsStreamAsync(deadline.Token)), deadline, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var body = new MemoryStream((int)Math.Min(content.Headers.ContentLength ?? ChunkBytes, 64 * ChunkBytes));
            byte[] chunk = new byte[ChunkBytes];
            while (true)
            {
                // Never asks for more than one byte past the limit: that byte proves the body too large, and nothing
                // after it is read.
                int wanted = (int)Math.Min(ChunkBytes, _maxResponseBytes - body.Length + 1);
                int read = await OnWireAsync(() => stream.ReadAsync(chunk.AsMemory(0, wanted), deadline.Token), deadline, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return new ReadOnlyMemory<byte>(body.GetBuffer(), 0, (int)body.Length);
                }

                // Outside the wire scope: the meter's own exceptions are the meter's, never a transport failure.
                _meter?.RecordResponseBytes(_shard.Id, read);
                if (body.Length + read > _maxResponseBytes)
                {
                    throw Failure(ShardCallFailure.ResponseTooLarge, "The response body exceeds the size limit.");
                }

                await body.WriteAsync(chunk.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
            }
        }
    }

    // Runs one wire operation; only its IO and HTTP errors are classified, as the token that fired or as a transport fault.
    private async ValueTask<T> OnWireAsync<T>(Func<ValueTask<T>> operation, CancellationTokenSource deadline, CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (IOException)
        {
            throw WireFailure(deadline, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw WireFailure(deadline, cancellationToken);
        }
    }

    // A wire error raised because a token fired is that cancellation or timeout, not a transport fault.
    private ShardCallException WireFailure(CancellationTokenSource deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return deadline.IsCancellationRequested
            ? Failure(ShardCallFailure.Timeout, "The call did not complete within its deadline.")
            : Failure(ShardCallFailure.Transport, "The connection to the backend failed.");
    }

    private ShardCallException Failure(ShardCallFailure failure, string reason, int? statusCode = null) =>
        new(_shard, failure, reason, statusCode);
}
