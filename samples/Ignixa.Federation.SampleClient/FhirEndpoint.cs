using System.Net.Http.Headers;
using System.Text.Json;

namespace Ignixa.Federation.SampleClient;

/// <summary>
/// One HTTPS endpoint (the federation, or one shard) with its own transport and its own credential.
/// Every request goes through <see cref="GetAsync"/>, which is the only place a token is attached and only after the
/// target passed the scope check, so a credential cannot reach any other scheme, host, port or path.
/// </summary>
internal sealed class FhirEndpoint : IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, ValueTask<string?>>? _accessToken;

    public FhirEndpoint(Uri baseUrl, Func<CancellationToken, ValueTask<string?>>? accessToken, HttpMessageHandler handler)
    {
        if (!baseUrl.IsAbsoluteUri
            || baseUrl.Scheme != Uri.UriSchemeHttps
            || baseUrl.UserInfo.Length > 0
            || baseUrl.Query.Length > 0
            || baseUrl.Fragment.Length > 0)
        {
            throw new ArgumentException(
                "A base URL must be absolute https without user info, query or fragment.", nameof(baseUrl));
        }

        // Ending the base path with '/' makes the scope check below a segment-boundary check.
        BaseUrl = baseUrl.AbsolutePath.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
        _accessToken = accessToken;
        _http = new HttpClient(handler);
    }

    public Uri BaseUrl { get; }

    /// <summary>
    /// True if <paramref name="target"/> is https, has no user info, and has the same host and effective port as the
    /// base and a path inside the base path (<c>/fhir/</c> contains <c>/fhir/Patient</c> but not <c>/fhirx/Patient</c>).
    /// </summary>
    public bool Contains(Uri target) =>
        target.IsAbsoluteUri
        && target.Scheme == Uri.UriSchemeHttps
        && target.UserInfo.Length == 0
        && string.Equals(target.Host, BaseUrl.Host, StringComparison.OrdinalIgnoreCase)
        && target.Port == BaseUrl.Port
        && target.AbsolutePath.StartsWith(BaseUrl.AbsolutePath, StringComparison.Ordinal);

    public async Task<JsonDocument> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        if (!Contains(url))
        {
            throw new LinkRejectedException(url, $"the endpoint {BaseUrl.GetLeftPart(UriPartial.Path)}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
        if (_accessToken is not null && await _accessToken(cancellationToken) is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        // AllowAutoRedirect is off, so a 3xx arrives here as a plain non-success status.
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new FederationRequestException(response.StatusCode, await ReadIssuesAsync(body, cancellationToken));
        }

        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The issues of an OperationOutcome body; empty if the body is anything else.</summary>
    private static async Task<IReadOnlyList<OperationIssue>> ReadIssuesAsync(Stream body, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            return document.RootElement.Str("resourceType") == "OperationOutcome"
                ? OperationIssue.ReadAll(document.RootElement)
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
