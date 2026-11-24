using System.Net;
using System.Net.Http.Headers;
using Ignixa.Federation.Api.Authentication;
using Ignixa.Federation.Api.Search;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Tests.Integration.Authentication;
using Ignixa.Federation.Tests.Integration.Continuations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ignixa.Federation.Tests.Integration.Search;

/// <summary>
/// The real API <c>Program</c> in memory, configured for the collision corpus through ordinary configuration keys, with
/// the scripted transport, the synthetic identity provider and an isolated Data Protection key ring.
/// </summary>
internal sealed class SearchHost : IDisposable
{
    private readonly WebApplicationFactory<Program> _root;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly IsolatedKeyRing? _ownedRing;

    private SearchHost(SearchHostSetup setup)
    {
        Shards = setup.Shards;
        IdentityProvider = setup.IdentityProvider;
        IDataProtectionProvider protection;
        if (setup.Protection is { } shared)
        {
            protection = shared;
        }
        else
        {
            _ownedRing = new IsolatedKeyRing();
            protection = _ownedRing.CreateProvider();
        }

        _root = new WebApplicationFactory<Program>();
        _factory = _root.WithWebHostBuilder(builder =>
        {
            foreach ((string key, string? value) in setup.Settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(protection);
                services.AddSingleton(TestSpecifications.R4);
                if (setup.Time is { } time)
                {
                    services.AddSingleton(time);
                }

                if (setup.Capabilities is { } capabilities)
                {
                    services.AddSingleton(capabilities);
                }

                if (setup.RegisterTransport)
                {
                    services.AddSingleton<IShardClientFactory>(setup.Shards);
                }

                if (setup.MaxRequestLineSize is { } maxRequestLine)
                {
                    services.Configure<KestrelServerOptions>(options => options.Limits.MaxRequestLineSize = maxRequestLine);
                }

                services.Configure<JwtBearerOptions>(
                    FederationCallerAuthentication.SchemeName,
                    options =>
                    {
                        options.BackchannelHttpHandler = setup.IdentityProvider;
                        setup.ConfigureJwtBearer?.Invoke(options);
                    });
                setup.ConfigureServices?.Invoke(services);
            });
        });
        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (setup.WaitForReadiness)
        {
            try
            {
                WaitForReadinessAsync().GetAwaiter().GetResult();
            }
            catch
            {
                Dispose();
                throw;
            }
        }
    }

    public ScriptedShards Shards { get; }

    public SyntheticIdentityProvider IdentityProvider { get; }

    public HttpClient Client { get; }

    public TestServer Server => _factory.Server;

    public IServiceProvider Services => _factory.Services;

    public async Task WaitForReadinessAsync()
    {
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        guard.CancelAfter(TimeSpan.FromSeconds(15));
        do
        {
            using HttpResponseMessage response = await Client.GetAsync(new Uri("/health/ready", UriKind.Relative), guard.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), guard.Token).ConfigureAwait(false);
        }
        while (true);
    }

    public static SearchHost Start(Action<SearchHostSetup>? configure = null)
    {
        var setup = new SearchHostSetup();
        configure?.Invoke(setup);
        return new SearchHost(setup);
    }

    /// <summary>Issues a token for the default caller (reader role, <see cref="SearchWorld.CallerObjectId"/>), or for the given request.</summary>
    public string Token(SyntheticTokenRequest? request = null) =>
        IdentityProvider.IssueToken(request ?? new SyntheticTokenRequest { Roles = [SearchWorld.ReaderRole], ObjectId = SearchWorld.CallerObjectId });

    /// <summary>Searches as the reader role.</summary>
    public Task<SearchResult> GetAsync(string pathAndQuery) => GetAsync(pathAndQuery, Token());

    /// <summary>Searches with the given bearer token, or anonymously.</summary>
    public Task<SearchResult> GetAsync(string pathAndQuery, string? token) =>
        GetCancellableAsync(pathAndQuery, token, TestContext.Current.CancellationToken);

    /// <summary>Searches with a caller-controlled cancellation token.</summary>
    public async Task<SearchResult> GetCancellableAsync(string pathAndQuery, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        return await SendCoreAsync(request, token, cancellationToken);
    }

    /// <summary>Follows an absolute public <c>next</c> link through the in-memory server, which is mounted at the base's path.</summary>
    public Task<SearchResult> FollowAsync(string next, string? token = null) => GetAsync(ToLocal(next), token ?? Token());

    public async Task<SearchResult> PostFormAsync(string pathAndQuery, string body, string? token = null, string mediaType = "application/x-www-form-urlencoded")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, pathAndQuery)
        {
            Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body)),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        return await SendAsync(request, token ?? Token());
    }

    /// <summary>Sends a request the caller owns.</summary>
    public Task<SearchResult> SendAsync(HttpRequestMessage request, string? token) =>
        SendCoreAsync(request, token, TestContext.Current.CancellationToken);

    private async Task<SearchResult> SendCoreAsync(HttpRequestMessage request, string? token, CancellationToken cancellationToken)
    {
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);
        return await SearchResult.ReadAsync(response);
    }

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        _root.Dispose();
        _ownedRing?.Dispose();
    }

    /// <summary>Maps an absolute public link onto the in-memory server, which serves the base path at its root.</summary>
    public static string ToLocal(string publicLink)
    {
        if (!publicLink.StartsWith(SearchWorld.PublicBase + "/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Not a public federation link.", nameof(publicLink));
        }

        return publicLink[SearchWorld.PublicBase.Length..];
    }
}

/// <summary>What a test host is built from. Every default is a valid, fully configured host.</summary>
internal sealed class SearchHostSetup
{
    /// <summary>Gets the configuration keys; tests edit or remove entries.</summary>
    public Dictionary<string, string?> Settings { get; } = SearchWorld.Settings().ToDictionary(p => p.Key, string? (p) => p.Value, StringComparer.Ordinal);

    /// <summary>Gets or sets the registered capabilities, or <see langword="null"/> to register none.</summary>
    public SearchCapabilities? Capabilities { get; set; } = SearchWorld.Capabilities();

    /// <summary>Gets or sets a value indicating whether the scripted transport is registered.</summary>
    public bool RegisterTransport { get; set; } = true;

    public ScriptedShards Shards { get; set; } = new();

    public SyntheticIdentityProvider IdentityProvider { get; set; } = new();

    public Action<JwtBearerOptions>? ConfigureJwtBearer { get; set; }

    public Action<IServiceCollection>? ConfigureServices { get; set; }

    public bool WaitForReadiness { get; set; } = true;

    /// <summary>Gets or sets a shared Data Protection provider; when unset the host gets its own isolated key ring.</summary>
    public IDataProtectionProvider? Protection { get; set; }

    /// <summary>Gets or sets the clock registered for the host, or <see langword="null"/> for the host's default.</summary>
    public TimeProvider? Time { get; set; }

    /// <summary>Gets or sets the Kestrel request-line limit the host is configured with, or <see langword="null"/> for Kestrel's default.</summary>
    public int? MaxRequestLineSize { get; set; }

    /// <summary>Removes every <c>Federation:Host</c> key, leaving the host unconfigured for search.</summary>
    public void Unconfigure()
    {
        foreach (string key in Settings.Keys.Where(k => k.StartsWith("Federation:Host:", StringComparison.Ordinal)).ToArray())
        {
            Settings.Remove(key);
        }
    }
}

/// <summary>A buffered response from the search host.</summary>
internal sealed class SearchResult
{
    private SearchResult(HttpStatusCode status, string? mediaType, string? charset, string body, IReadOnlyList<string> challenges)
    {
        StatusCode = status;
        MediaType = mediaType;
        Charset = charset;
        Body = body;
        Challenges = challenges;
    }

    public HttpStatusCode StatusCode { get; }

    public string? MediaType { get; }

    public string? Charset { get; }

    public string Body { get; }

    public IReadOnlyList<string> Challenges { get; }

    public System.Text.Json.JsonElement Json => System.Text.Json.JsonElement.Parse(Body);

    /// <summary>Gets each entry's <c>fullUrl</c> and the exact raw text of its <c>resource</c>, in order.</summary>
    public IReadOnlyList<(string FullUrl, string Resource)> Entries =>
        Json.TryGetProperty("entry", out System.Text.Json.JsonElement entries)
            ? [.. entries.EnumerateArray().Select(e => (e.GetProperty("fullUrl").GetString()!, e.GetProperty("resource").GetRawText()))]
            : [];

    /// <summary>Gets the URL of the named link, or <see langword="null"/>.</summary>
    public string? Link(string relation) =>
        Json.TryGetProperty("link", out System.Text.Json.JsonElement links)
            ? links.EnumerateArray()
                .Where(l => l.GetProperty("relation").GetString() == relation)
                .Select(l => l.GetProperty("url").GetString())
                .SingleOrDefault()
            : null;

    /// <summary>Gets the first OperationOutcome issue's code.</summary>
    public string? IssueCode =>
        Json.GetProperty("resourceType").GetString() == "OperationOutcome"
            ? Json.GetProperty("issue")[0].GetProperty("code").GetString()
            : null;

    public static async Task<SearchResult> ReadAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return new SearchResult(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            response.Content.Headers.ContentType?.CharSet,
            body,
            [.. response.Headers.WwwAuthenticate.Select(h => h.ToString())]);
    }
}
