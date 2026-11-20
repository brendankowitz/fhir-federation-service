using System.Security.Cryptography.X509Certificates;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Http;

/// <summary>Routes each search, continuation and read to the registered shard that owns it (design spec §4, §5.1, §7 and §12).</summary>
/// <remarks>
/// <para><b>Routing.</b> The owner is the <see cref="ShardId"/> of the first-search request, the cursor or the qualified
/// read key, and nothing else: a cursor's target URL is never consulted to choose a shard. The owner's
/// <see cref="HttpShardClient"/> then applies its own checks, so a target that is not a continuation of that shard's
/// search is refused there and is never re-routed to another shard. An owner the registry does not hold is refused with an
/// <see cref="ArgumentException"/> before any credential is requested or anything is sent.</para>
/// <para><b>Clients.</b> One <see cref="HttpShardClient"/> per registered shard is built at construction and reused for every
/// call, so each shard keeps its own connection pool and circuit breaker. Every other behaviour (deadline, response size
/// limit, metering, cancellation, failures and the read result) is that client's, unchanged.</para>
/// <para><b>Per-request metering.</b> <see cref="ForRequest"/> lends a view that charges one request's meter instead of the
/// router's own, over the same clients, pools and breakers. Calls made directly on the router or through
/// <see cref="GetClient"/> keep using the router's meter, so a router built without one leaves background work uncharged.</para>
/// <para><b>Lifetime.</b> Register one instance for the life of the process and dispose it at teardown, after use has
/// stopped. It disposes only the clients it created; the credential provider and the transfer meter belong to the caller.
/// A call that starts after disposal throws <see cref="ObjectDisposedException"/>.</para>
/// <para>Calls may run concurrently before teardown. Calls on the same shard share that shard's resilience state.</para>
/// <para><b>Request limits.</b> Every registered shard has exactly one explicit <see cref="ShardRequestLimits"/>; the map
/// is copied at construction, and a missing, extra, duplicate or null entry is refused. The shard's own client, including
/// the one <see cref="GetClient"/> lends, applies them (see <see cref="HttpShardClient"/>).</para>
/// </remarks>
public sealed class MultiShardHttpClient : IShardClient, IDisposable
{
    private readonly Dictionary<ShardId, HttpShardClient> _clients;
    private int _disposed;

    /// <summary>Creates a router with one client per shard of <paramref name="registry"/>, trusting the platform's certificate store.</summary>
    /// <param name="registry">The shards to route to.</param>
    /// <param name="credentials">Acquires each shard's bearer token, once per call; not owned or disposed by the router.</param>
    /// <param name="callTimeout">The deadline for one whole call, from credential acquisition to the last body byte.</param>
    /// <param name="maxResponseBytes">The largest response body one call reads.</param>
    /// <param name="limits">
    /// The configured search ceilings, with exactly one entry for every shard of
    /// <paramref name="registry"/>. The router copies the entries, so later changes to the map have no effect, and keys are
    /// matched by <see cref="ShardId"/> equality whatever comparer the map uses.
    /// </param>
    /// <param name="meter">Observes actual sends and response bytes, or <see langword="null"/>; not owned or disposed by the router.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/>, <paramref name="credentials"/> or <paramref name="limits"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="limits"/> lacks an entry for a registered shard, names a shard the registry does not hold, repeats a
    /// shard, or has a <see langword="null"/> value.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="callTimeout"/> or <paramref name="maxResponseBytes"/> is outside what <see cref="HttpShardClient"/> accepts.
    /// </exception>
    public MultiShardHttpClient(
        ShardRegistry registry,
        IShardCredentialProvider credentials,
        TimeSpan callTimeout,
        long maxResponseBytes,
        IReadOnlyDictionary<ShardId, ShardRequestLimits> limits,
        IShardTransferMeter? meter = null)
        : this(registry, credentials, callTimeout, maxResponseBytes, limits, meter, trustedTestCertificate: null)
    {
    }

    // Tests trust one generated loopback leaf through the client's own test seam; production never passes one.
    internal MultiShardHttpClient(
        ShardRegistry registry,
        IShardCredentialProvider credentials,
        TimeSpan callTimeout,
        long maxResponseBytes,
        IReadOnlyDictionary<ShardId, ShardRequestLimits> limits,
        IShardTransferMeter? meter,
        X509Certificate2? trustedTestCertificate)
        : this(registry, ClientFactory(registry, credentials, callTimeout, maxResponseBytes, limits, meter, trustedTestCertificate))
    {
    }

    // A client that fails to build leaves the ones already built unowned, so they are released before the error escapes.
    internal MultiShardHttpClient(ShardRegistry registry, Func<ShardDescriptor, HttpShardClient> createClient)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _clients = new Dictionary<ShardId, HttpShardClient>(registry.Shards.Count);
        bool complete = false;
        try
        {
            foreach (ShardDescriptor shard in registry.Shards)
            {
                _clients.Add(shard.Id, createClient(shard));
            }

            complete = true;
        }
        finally
        {
            if (!complete)
            {
                DisposeClients();
            }
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The request's shard is not registered, or the owning client refuses the request. Nothing is sent.</exception>
    /// <exception cref="ObjectDisposedException">The router is disposed.</exception>
    /// <exception cref="ShardCallException">
    /// The exchange failed, the response was not an exact answer to the request, or the request does not fit the shard's
    /// <see cref="ShardRequestLimits"/> (<see cref="ShardCallFailure.RequestTooLarge"/>, nothing sent).
    /// </exception>
    public Task<ShardPage> SearchAsync(ShardSearchRequest request, CancellationToken cancellationToken) =>
        SearchWithContextAsync(request, context: default, cancellationToken);

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="cursor"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The cursor's shard is not registered, or the owning client refuses its target. A target that points at another shard
    /// is refused, not followed there. Nothing is sent.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The router is disposed.</exception>
    /// <exception cref="ShardCallException">
    /// The exchange failed or the response was not a readable page, or the redemption does not fit the shard's
    /// <see cref="ShardRequestLimits"/> (<see cref="ShardCallFailure.InvalidNextLink"/>, nothing sent).
    /// </exception>
    public Task<ShardPage> ContinueAsync(ShardCursor cursor, CancellationToken cancellationToken) =>
        ContinueWithContextAsync(cursor, context: default, cancellationToken);

    /// <summary>Reads the current version of one resource from the shard its key names; see <see cref="HttpShardClient.ReadAsync"/>.</summary>
    /// <param name="key">The qualified resource to read.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The resource exactly as its shard returned it, with the URL it was read from.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The key's shard is not registered, or the owning client refuses the key. Nothing is sent.</exception>
    /// <exception cref="ObjectDisposedException">The router is disposed.</exception>
    /// <exception cref="ShardCallException">
    /// The exchange failed, or the shard reported <see cref="ShardCallFailure.ResourceNotFound"/> or
    /// <see cref="ShardCallFailure.ResourceGone"/>.
    /// </exception>
    public Task<ShardReadResult> ReadAsync(ResourceKey key, CancellationToken cancellationToken) =>
        ReadWithContextAsync(key, context: default, cancellationToken);

    /// <summary>
    /// Lends the router's client for one registered shard, so a component such as the metadata worker uses the same
    /// connection pool and credential path rather than building a second transport.
    /// </summary>
    /// <param name="shard">The shard's descriptor, exactly as held by the registry this router was built from.</param>
    /// <returns>
    /// The shard's single client, the same instance on every call. The router owns it: the caller must not dispose it, and
    /// must stop using it before the router is disposed, after which it is closed.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="shard"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="shard"/> is not the registry's own descriptor. A descriptor from another registry is refused even
    /// when it has the same shard id.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The router is disposed.</exception>
    public HttpShardClient GetClient(ShardDescriptor shard)
    {
        ArgumentNullException.ThrowIfNull(shard);
        ThrowIfDisposed();
        return _clients.TryGetValue(shard.Id, out HttpShardClient? client) && client.IsFor(shard)
            ? client
            : throw new ArgumentException("The descriptor is not one of this router's registered shards.", nameof(shard));
    }

    /// <summary>
    /// Borrows a view of this router whose calls are charged to <paramref name="meter"/> instead of the router's own meter,
    /// so one request's transfer budget sees only that request's sends and response bytes.
    /// </summary>
    /// <param name="meter">
    /// The request's meter. It must be thread-safe because parallel shard calls can invoke it concurrently.
    /// </param>
    /// <returns>
    /// A lightweight view over this router's own clients, so it adds no HTTP client, connection pool or circuit breaker, and
    /// shares their state with every other view and with the router. The view is not disposable and must not outlive the router.
    /// Search, continuation and read each charge only <paramref name="meter"/>, including every retry, never both meters.
    /// </returns>
    /// <remarks>
    /// Build the router with no meter (<see langword="null"/>) when the host wants background work, such as metadata
    /// discovery through <see cref="GetClient"/>, to stay uncharged: the router's own meter is the only one those calls use.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="meter"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The router is disposed.</exception>
    public RequestShardClient ForRequest(IShardTransferMeter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ThrowIfDisposed();
        return new RequestShardClient(this, new ShardRequestContext(meter, callerId: null));
    }

    /// <summary>
    /// Borrows a metered request view that sends an explicit <c>X-Federation-Caller</c> value on its shard data calls.
    /// </summary>
    /// <param name="meter">The thread-safe meter for this request.</param>
    /// <param name="callerId">
    /// A host-issued opaque identifier for correlation, not an inbound header, raw identity or credential.
    /// Supply 1 to 128 ASCII letters, digits or <c>-._~</c> characters. The value is sent unchanged.
    /// </param>
    /// <returns>A view over this router; its retry, continuation and read calls keep the same identifier.</returns>
    /// <remarks>Unbound router calls, metadata and credential acquisition do not use this identifier.</remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The caller identifier is not in the supported form.</exception>
    /// <exception cref="ObjectDisposedException">The router is disposed.</exception>
    public RequestShardClient ForRequestWithCaller(IShardTransferMeter meter, string callerId)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(callerId);
        ThrowIfDisposed();
        if (callerId.Length is 0 or > 128 ||
            callerId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_' or '~')))
        {
            throw new ArgumentException("The opaque caller identifier must be 1 to 128 ASCII letters, digits or -._~ characters.", nameof(callerId));
        }

        return new RequestShardClient(this, new ShardRequestContext(meter, callerId));
    }

    internal Task<ShardPage> SearchWithContextAsync(ShardSearchRequest request, ShardRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OwnerOf(request.Shard, nameof(request)).SearchWithContextAsync(request, context, cancellationToken);
    }

    internal Task<ShardPage> ContinueWithContextAsync(ShardCursor cursor, ShardRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return OwnerOf(cursor.Shard, nameof(cursor)).ContinueWithContextAsync(cursor, context, cancellationToken);
    }

    internal Task<ShardReadResult> ReadWithContextAsync(ResourceKey key, ShardRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return OwnerOf(key.Shard, nameof(key)).ReadWithContextAsync(key, context, cancellationToken);
    }

    /// <summary>Disposes the clients the router created, and not the credential provider or the transfer meter. Calling it again has no effect.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            DisposeClients();
        }
    }

    private static Func<ShardDescriptor, HttpShardClient> ClientFactory(
        ShardRegistry registry,
        IShardCredentialProvider credentials,
        TimeSpan callTimeout,
        long maxResponseBytes,
        IReadOnlyDictionary<ShardId, ShardRequestLimits> limits,
        IShardTransferMeter? meter,
        X509Certificate2? trustedTestCertificate)
    {
        // Copied under this router's own equality, so a caller's comparer or later edits cannot change which shard a limit binds to.
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(limits);
        var bound = new Dictionary<ShardId, ShardRequestLimits>(registry.Shards.Count);
        foreach ((ShardId id, ShardRequestLimits value) in limits)
        {
            if (value is null || !bound.TryAdd(id, value))
            {
                throw new ArgumentException("The limits must hold one non-null entry per shard.", nameof(limits));
            }
        }

        if (bound.Count != registry.Shards.Count || registry.Shards.Any(shard => !bound.ContainsKey(shard.Id)))
        {
            throw new ArgumentException("The limits must hold exactly one entry for every registered shard.", nameof(limits));
        }

        return shard => new HttpShardClient(shard, credentials, callTimeout, maxResponseBytes, bound[shard.Id], meter, trustedTestCertificate);
    }

    private HttpShardClient OwnerOf(ShardId shard, string parameterName)
    {
        ThrowIfDisposed();
        return _clients.TryGetValue(shard, out HttpShardClient? client)
            ? client
            : throw new ArgumentException("The shard is not registered.", parameterName);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private void DisposeClients()
    {
        foreach (HttpShardClient client in _clients.Values)
        {
            client.Dispose();
        }
    }
}
