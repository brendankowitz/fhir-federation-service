using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Text;
using System.Web;

namespace Ignixa.Federation.Shards.UnitTests.Credentials;

/// <summary>
/// A synthetic Entra token endpoint for the real SDK credential. It is the whole transport: there is no inner handler, so a
/// request it does not recognise is recorded and refused, never forwarded to a network. Each recognised client-credentials
/// exchange issues a fresh synthetic token.
/// </summary>
internal sealed class InMemoryTokenEndpoint : HttpMessageHandler
{
    private const string PublicCloudHost = "login.microsoftonline.com";

    private const string InstanceDiscoveryDocument = """
        {"tenant_discovery_endpoint":"https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration","api-version":"1.1",
        "metadata":[{"preferred_network":"login.microsoftonline.com","preferred_cache":"login.windows.net",
        "aliases":["login.microsoftonline.com","login.windows.net","login.microsoft.com","sts.windows.net"]}]}
        """;

    private readonly Lock _gate = new();
    private readonly List<TokenExchange> _exchanges = [];
    private readonly List<string> _unexpected = [];

    /// <summary>Gets or sets the lifetime the next issued tokens declare, in seconds.</summary>
    public int ExpiresInSeconds { get; set; } = 3600;

    public IReadOnlyList<TokenExchange> Exchanges
    {
        get
        {
            lock (_gate)
            {
                return [.. _exchanges];
            }
        }
    }

    public IReadOnlyList<string> Unexpected
    {
        get
        {
            lock (_gate)
            {
                return [.. _unexpected];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri!;
        string[] path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool isPublicCloud = uri.Scheme == Uri.UriSchemeHttps && uri.Host == PublicCloudHost;
        if (isPublicCloud && request.Method == HttpMethod.Get && path is ["common", "discovery", "instance"])
        {
            // The SDK's authority metadata lookup, answered as Entra answers it for the public cloud. It is not a token
            // exchange and MSAL caches it process-wide, so it is neither counted nor refused.
            return Json(InstanceDiscoveryDocument);
        }

        bool isTokenRequest = isPublicCloud && request.Method == HttpMethod.Post && path is [_, "oauth2", "v2.0", "token"];
        if (!isTokenRequest)
        {
            lock (_gate)
            {
                _unexpected.Add($"{request.Method} {uri.GetLeftPart(UriPartial.Path)}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        NameValueCollection form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
        TokenExchange exchange;
        lock (_gate)
        {
            exchange = new TokenExchange(
                path[0],
                form["grant_type"],
                form["client_id"],
                form["client_secret"],
                form["scope"],
                $"synthetic.sdk.token.{_exchanges.Count + 1}");
            _exchanges.Add(exchange);
        }

        string json = string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"token_type":"Bearer","expires_in":{{ExpiresInSeconds}},"ext_expires_in":{{ExpiresInSeconds}},"access_token":"{{exchange.Token}}"}""");
        return Json(json);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    internal sealed record TokenExchange(string Tenant, string? GrantType, string? ClientId, string? ClientSecret, string? Scope, string Token);
}
