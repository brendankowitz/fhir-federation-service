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
/// <para><b>Lifetime.</b> Register one instance for the life of the process and dispose it at teardown, after use has
/// stopped. It disposes only the clients it created; the credential provider and the transfer meter belong to the caller.
/// A call that starts after disposal throws <see cref="ObjectDisposedException"/>.</para>
/// <para>Calls may run concurrently before teardown. Calls on the same shard share that shard's resilience state.</para>
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
    /// <param name="meter">Observes actual sends and response bytes, or <see langword="null"/>; not owned or disposed by the router.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> or <paramref name="credentials"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="callTimeout"/> or <paramref name="maxResponseBytes"/> is outside what <see cref="HttpShardClient"/> accepts.
    /// </exception>
    public MultiShardHttpClient(
        ShardRegistry registry,
        IShardCredentialProvider credentials,
        TimeSpan callTimeout,
        long maxResponseBytes,
        IShardTransferMeter? meter = null)
        : this(registry, credentials, callTimeout, maxResponseBytes, meter, trustedTestCertificate: null)
    {
    }

    // Tests trust one generated loopback leaf through the client's own test seam; production never passes one.
    internal MultiShardHttpClient(
        ShardRegistry registry,
        IShardCredentialProvider credentials,
        TimeSpan callTimeout,
        long maxResponseBytes,
        IShardTransferMeter? meter,
        X509Certificate2? trustedTestCertificate)
        : this(registry, shard => new HttpShardClient(shard, credentials, callTimeout, maxResponseBytes, meter, trustedTestCertificate))
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
    /// <exception cref="ShardCallException">The exchange failed or the response was not an exact answer to the request.</exception>
    public Task<ShardPage> SearchAsync(ShardSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OwnerOf(request.Shard, nameof(request)).SearchAsync(request, cancellationToken);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="cursor"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The cursor's shard is not registered, or the owning client refuses its target. A target that points at another shard
    /// is refused, not followed there. Nothing is sent.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The router is disposed.</exception>
    /// <exception cref="ShardCallException">The exchange failed or the response was not a readable page.</exception>
    public Task<ShardPage> ContinueAsync(ShardCursor cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return OwnerOf(cursor.Shard, nameof(cursor)).ContinueAsync(cursor, cancellationToken);
    }

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
    public Task<ShardReadResult> ReadAsync(ResourceKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return OwnerOf(key.Shard, nameof(key)).ReadAsync(key, cancellationToken);
    }

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

    /// <summary>Disposes the clients the router created, and not the credential provider or the transfer meter. Calling it again has no effect.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            DisposeClients();
        }
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
