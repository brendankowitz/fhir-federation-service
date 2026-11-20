using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Diagnostics;

namespace Ignixa.Federation.Shards.Http;

/// <summary>Searches one shard over HTTPS, one backend exchange per page (design spec §7).</summary>
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
/// would repeat the query just sent is refused, so a backend cannot loop a stream.</para>
/// <para><b>Limits.</b> Each call, from credential acquisition to the last body byte, has the deadline and response size
/// limit given at construction. The optional <see cref="IShardTransferMeter"/> sees each actual send and every body byte
/// read, so a caller can enforce an aggregate budget.</para>
/// <para><b>Failures.</b> A call either returns a page that represents the backend's answer exactly or throws: a
/// classified <see cref="ShardCallException"/>, an <see cref="OperationCanceledException"/> when the caller cancels, or
/// the credential provider's or meter's own exception. It never returns an empty page for a failure, and never retries.</para>
/// <para>Thread-safe: concurrent calls are independent.</para>
/// </remarks>
public sealed class HttpShardClient : IShardClient, IDisposable
{
    private const int ChunkBytes = 16 * 1024;
    private const string Stage = "search";

    // CancellationTokenSource.CancelAfter accepts at most uint.MaxValue - 1 milliseconds.
    private static readonly TimeSpan MaxCallTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private static readonly UriCreationOptions Verbatim = new() { DangerousDisablePathAndQueryCanonicalization = true };

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

    // Redirects, cookies, decompression and ambient credentials are off: the client sends exactly what it builds and
    // reads exactly what the backend returns.
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
        if (questionMark < 0)
        {
            throw Failure(ShardCallFailure.SelfLinkMismatch, "The self link does not report the search that was sent.");
        }

        ReadOnlySpan<char> selfQuery = self.AsSpan(questionMark + 1);
        int fragment = selfQuery.IndexOf('#');
        if (fragment >= 0)
        {
            selfQuery = selfQuery[..fragment];
        }

        if (!QueryText.TryDecodePairs(selfQuery, out List<(string Key, string Value)> reported))
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
            ReadOnlyMemory<byte> body = await ExchangeAsync(message, cancellationToken).ConfigureAwait(false);
            return interpret(Searchset.Read(body, _shard, type));
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

    private async Task<ReadOnlyMemory<byte>> ExchangeAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_callTimeout);
        try
        {
            string token = await _credentials.GetAccessTokenAsync(_shard.Auth, deadline.Token).ConfigureAwait(false);
            if (!IsBearerToken(token))
            {
                // The token is deliberately not quoted.
                throw new InvalidOperationException("The credential provider returned text that is not a bearer token.");
            }

            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
            message.Headers.TryAddWithoutValidation("Prefer", "handling=strict");

            _meter?.RecordSend(_shard.Id);
            HttpResponseMessage response;
            try
            {
                response = await _invoker.SendAsync(message, deadline.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                throw WireFailure(deadline, cancellationToken);
            }

            using (response)
            {
                CheckStatus(response);
                return await ReadBodyAsync(response.Content, deadline, cancellationToken).ConfigureAwait(false);
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

    private void CheckStatus(HttpResponseMessage response)
    {
        int status = (int)response.StatusCode;
        ShardCallFailure? failure = status switch
        {
            200 => null,
            >= 300 and < 400 => ShardCallFailure.Redirect,
            401 or 403 => ShardCallFailure.Authentication,
            _ => ShardCallFailure.UnexpectedStatus,
        };

        // The body of a failed exchange is never read: it may echo the query or hold backend detail.
        if (failure is { } classified)
        {
            throw Failure(classified, "The backend did not answer with a search page.", status);
        }
    }

    private async Task<ReadOnlyMemory<byte>> ReadBodyAsync(HttpContent content, CancellationTokenSource deadline, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > _maxResponseBytes)
        {
            throw Failure(ShardCallFailure.ResponseTooLarge, "The response body exceeds the size limit.");
        }

        try
        {
            Stream stream = await content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var body = new MemoryStream((int)Math.Min(content.Headers.ContentLength ?? ChunkBytes, 64 * ChunkBytes));
                byte[] chunk = new byte[ChunkBytes];
                int read;
                while ((read = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    _meter?.RecordResponseBytes(_shard.Id, read);
                    if (body.Length + read > _maxResponseBytes)
                    {
                        throw Failure(ShardCallFailure.ResponseTooLarge, "The response body exceeds the size limit.");
                    }

                    await body.WriteAsync(chunk.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                }

                return new ReadOnlyMemory<byte>(body.GetBuffer(), 0, (int)body.Length);
            }
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
