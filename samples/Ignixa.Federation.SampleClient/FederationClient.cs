using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ignixa.Federation.SampleClient;

/// <summary>
/// A client for the FHIR federation. It follows the federation's opaque <c>next</c> links, treats <c>fullUrl</c> as
/// resource identity, and keeps each credential on its own endpoint: the federation token goes only to the federation,
/// and a shard is read directly only if it was configured, with that shard's own token. It never follows redirects,
/// retries, or restarts an expired search.
/// </summary>
/// <remarks>
/// The client owns the handlers it is given (or creates) and disposes them. A supplied handler must have
/// auto-redirect off; if you wrap a handler in a <see cref="DelegatingHandler"/>, that is yours to check.
/// </remarks>
internal sealed class FederationClient : IDisposable
{
    private readonly ScopedEndpoint _federation;
    private readonly List<ScopedEndpoint> _shards;

    /// <param name="options">Endpoints and credentials.</param>
    /// <param name="federationHandler">Transport for the federation; by default a plain no-redirect handler.</param>
    /// <param name="shardHandlerFactory">
    /// Creates the transport for a shard, given its base URL; by default a plain no-redirect handler. Each shard gets
    /// its own transport.
    /// </param>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "NoRedirects returns the handler, and ScopedEndpoint takes ownership of it and disposes it.")]
    public FederationClient(
        FederationClientOptions options,
        HttpMessageHandler? federationHandler = null,
        Func<Uri, HttpMessageHandler>? shardHandlerFactory = null)
    {
        _federation = new ScopedEndpoint(options.FederationBaseUrl, options.FederationAccessToken, NoRedirects(federationHandler));
        _shards =
        [
            .. options.DirectReadShards.Select(shard => new ScopedEndpoint(
                shard.BaseUrl, shard.AccessToken, NoRedirects(shardHandlerFactory?.Invoke(shard.BaseUrl)))),
        ];

        // A URL must belong to exactly one endpoint, or it could be sent with the wrong credential.
        List<ScopedEndpoint> all = [_federation, .. _shards];
        for (var i = 0; i < all.Count; i++)
        {
            for (var j = i + 1; j < all.Count; j++)
            {
                if (all[i].Contains(all[j].BaseUrl) || all[j].Contains(all[i].BaseUrl))
                {
                    throw new ArgumentException("The federation and shard base URLs must not overlap.", nameof(options));
                }
            }
        }
    }

    /// <summary>
    /// Runs <c>GET [base]/[type]?…</c> and yields one page at a time, following each <c>next</c> link as the federation
    /// gave it (as parsed by <see cref="Uri"/>; the client never decodes or rebuilds it) until there is none. Parameter
    /// and type problems throw immediately; network and server problems throw while enumerating.
    /// </summary>
    /// <exception cref="SearchExpiredException">A continuation was rejected with 410; start a new search.</exception>
    /// <exception cref="LinkRejectedException">A <c>next</c> link points outside the federation.</exception>
    public IAsyncEnumerable<SearchPage> SearchAsync(
        string resourceType,
        IEnumerable<KeyValuePair<string, string>> parameters,
        CancellationToken cancellationToken = default)
    {
        if (resourceType.Length == 0 || !resourceType.All(char.IsAsciiLetter))
        {
            throw new ArgumentException("The resource type must be letters only, for example 'Patient'.", nameof(resourceType));
        }

        List<string> pairs = [];
        foreach (var (name, value) in parameters)
        {
            // Continuation is only ever requested through a next link the federation issued.
            if (name == "ct")
            {
                throw new ArgumentException("The 'ct' parameter is issued by the federation and cannot be supplied.", nameof(parameters));
            }

            pairs.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");
        }

        var query = pairs.Count > 0 ? "?" + string.Join('&', pairs) : "";
        return PagesAsync(new Uri(_federation.BaseUrl, resourceType + query), cancellationToken);
    }

    /// <summary>
    /// Reads a match or include through the federation's proxied read (<see cref="FederatedEntry.Alternate"/>), using
    /// the federation credential. For clients that can reach the federation but not the shard. The caller disposes the result.
    /// </summary>
    public async Task<JsonDocument> ReadViaFederationAsync(FederatedEntry entry, CancellationToken cancellationToken = default) =>
        await _federation.GetAsync(
            entry.Alternate ?? throw new InvalidOperationException(
                "The entry has no alternate link; the federation may have proxied reads disabled."),
            cancellationToken);

    /// <summary>
    /// Reads an entry straight from its shard at <see cref="FederatedEntry.FullUrl"/>, with that shard's own
    /// credential (never the federation's). Only shards listed in <see cref="FederationClientOptions.DirectReadShards"/>
    /// are allowed. The caller disposes the result.
    /// </summary>
    /// <exception cref="LinkRejectedException">The entry's shard is not configured; nothing was sent.</exception>
    public async Task<JsonDocument> ReadDirectAsync(FederatedEntry entry, CancellationToken cancellationToken = default)
    {
        var shard = _shards.FirstOrDefault(s => s.Contains(entry.FullUrl))
            ?? throw new LinkRejectedException(entry.FullUrl, "every configured direct-read shard");

        return await shard.GetAsync(entry.FullUrl, cancellationToken);
    }

    public void Dispose()
    {
        _federation.Dispose();
        foreach (var shard in _shards)
        {
            shard.Dispose();
        }
    }

    private async IAsyncEnumerable<SearchPage> PagesAsync(Uri first, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HashSet<Uri> followed = [first];
        var url = first;
        var isContinuation = false;

        while (true)
        {
            var page = await FetchPageAsync(url, isContinuation, cancellationToken);
            yield return page;

            if (page.NextLink is not { } next)
            {
                yield break;
            }

            if (!followed.Add(next))
            {
                throw new InvalidDataException("The federation returned a next link that was already followed; stopping.");
            }

            url = next;
            isContinuation = true;
        }
    }

    private async Task<SearchPage> FetchPageAsync(Uri url, bool isContinuation, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await _federation.GetAsync(url, cancellationToken);
            return SearchPage.Parse(document.RootElement, _federation.BaseUrl);
        }
        catch (FederationRequestException ex) when (isContinuation && ex.StatusCode == HttpStatusCode.Gone)
        {
            throw new SearchExpiredException(ex.Issues);
        }
    }

    /// <summary>The transport used when the caller supplies none: a plain handler that never follows redirects.</summary>
    internal static SocketsHttpHandler CreateDefaultHandler() => new() { AllowAutoRedirect = false };

    private static HttpMessageHandler NoRedirects(HttpMessageHandler? handler) => handler switch
    {
        null => CreateDefaultHandler(),
        HttpClientHandler { AllowAutoRedirect: true } or SocketsHttpHandler { AllowAutoRedirect: true } =>
            throw new ArgumentException("The handler follows redirects; set AllowAutoRedirect = false.", nameof(handler)),
        _ => handler,
    };
}
