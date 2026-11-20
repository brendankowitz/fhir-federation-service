using Azure.Core.Pipeline;
using Azure.Identity;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Credentials;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Credentials;

/// <summary>
/// What the real Azure SDK does with the credentials the provider keeps: a real <see cref="ClientSecretCredential"/>, built
/// with the production options, talks to an <see cref="InMemoryTokenEndpoint"/> instead of Entra. These tests count actual
/// SDK token exchanges; which credential is selected is covered by the recording-factory tests.
/// </summary>
public sealed class AzureShardCredentialProviderSdkTests : IDisposable
{
    private const string Tenant = "7d3c1a52-9b0e-4c6f-8a21-5e4f3b2d1c0a";
    private const string Client = "c1d2e3f4-a5b6-4789-8abc-def012345678";

    private readonly InMemoryTokenEndpoint _endpoint = new();
    private readonly List<HttpClient> _clients = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RepeatedAcquisitionsForOneScopeCauseOneTokenExchangeWhileTheSecretIsStillResolvedEachTime()
    {
        var secrets = new ScriptedSecretResolver("synthetic-sdk-secret-v1");
        AzureShardCredentialProvider provider = Provider(secrets);
        ClientCredentialsAuth auth = SyntheticAuths.Client("api://synthetic-shard", Tenant, Client, "kv-sdk-secret");

        string warm = await provider.GetAccessTokenAsync(auth, Ct);
        string[] later = [
            await provider.GetAccessTokenAsync(auth, Ct),
            await provider.GetAccessTokenAsync(auth, Ct),
            await provider.GetAccessTokenAsync(auth, Ct),
        ];

        InMemoryTokenEndpoint.TokenExchange exchange = _endpoint.Exchanges.ShouldHaveSingleItem();
        exchange.ShouldBe(new InMemoryTokenEndpoint.TokenExchange(
            Tenant, "client_credentials", Client, "synthetic-sdk-secret-v1", "api://synthetic-shard/.default", warm));
        later.ShouldAllBe(token => token == warm);
        secrets.Requested.Count.ShouldBe(4);
        _endpoint.Unexpected.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADistinctScopeOnTheSameCredentialHasItsOwnExchangeAndCacheEntry()
    {
        AzureShardCredentialProvider provider = Provider(new ScriptedSecretResolver("synthetic-sdk-secret-v1"));
        ClientCredentialsAuth audienceA = SyntheticAuths.Client("api://synthetic-shard-a", Tenant, Client, "kv-sdk-secret");
        ClientCredentialsAuth audienceB = SyntheticAuths.Client("https://shard-b.synthetic.example/", Tenant, Client, "kv-sdk-secret");

        string a1 = await provider.GetAccessTokenAsync(audienceA, Ct);
        string b1 = await provider.GetAccessTokenAsync(audienceB, Ct);
        string a2 = await provider.GetAccessTokenAsync(audienceA, Ct);
        string b2 = await provider.GetAccessTokenAsync(audienceB, Ct);

        _endpoint.Exchanges.Select(e => e.Scope).ShouldBe(["api://synthetic-shard-a/.default", "https://shard-b.synthetic.example//.default"]);
        a1.ShouldNotBe(b1);
        a2.ShouldBe(a1);
        b2.ShouldBe(b1);
        _endpoint.Unexpected.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARotatedSecretIsExchangedOnceAndThenItsTokenIsReused()
    {
        var secrets = new ScriptedSecretResolver("synthetic-sdk-secret-v1");
        AzureShardCredentialProvider provider = Provider(secrets);
        ClientCredentialsAuth auth = SyntheticAuths.Client("api://synthetic-shard", Tenant, Client, "kv-sdk-secret");

        string v1 = await provider.GetAccessTokenAsync(auth, Ct);
        await provider.GetAccessTokenAsync(auth, Ct);
        secrets.Value = "synthetic-sdk-secret-v2";
        string v2 = await provider.GetAccessTokenAsync(auth, Ct);
        string v2Again = await provider.GetAccessTokenAsync(auth, Ct);

        _endpoint.Exchanges.Select(e => e.ClientSecret).ShouldBe(["synthetic-sdk-secret-v1", "synthetic-sdk-secret-v2"]);
        v2.ShouldNotBe(v1);
        v2Again.ShouldBe(v2);
        _endpoint.Unexpected.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATokenInsideTheSdkRefreshWindowIsRefreshedByTheNextAcquisitionAndTheRefreshedOneIsReused()
    {
        // The SDK treats a token within five minutes of expiry as due for refresh, so a one-minute token is refreshed
        // by the very next acquisition without any waiting.
        _endpoint.ExpiresInSeconds = 60;
        AzureShardCredentialProvider provider = Provider(new ScriptedSecretResolver("synthetic-sdk-secret-v1"));
        ClientCredentialsAuth auth = SyntheticAuths.Client("api://synthetic-shard", Tenant, Client, "kv-sdk-secret");

        string expiring = await provider.GetAccessTokenAsync(auth, Ct);
        _endpoint.ExpiresInSeconds = 3600;
        string refreshed = await provider.GetAccessTokenAsync(auth, Ct);
        string reused = await provider.GetAccessTokenAsync(auth, Ct);

        _endpoint.Exchanges.Select(e => e.Token).ShouldBe([expiring, refreshed]);
        refreshed.ShouldNotBe(expiring);
        reused.ShouldBe(refreshed);
        _endpoint.Unexpected.ShouldBeEmpty();
    }

    public void Dispose()
    {
        foreach (HttpClient client in _clients)
        {
            client.Dispose();
        }

        _endpoint.Dispose();
    }

    private AzureShardCredentialProvider Provider(IShardSecretResolver secrets) =>
        new(secrets, _ => throw new NotSupportedException("Only client credentials are exercised."), RealClientSecret);

    // The production options (public-cloud authority, diagnostics off) with only the transport replaced.
    private ClientSecretCredential RealClientSecret(string tenantId, string clientId, string secret)
    {
        var http = new HttpClient(_endpoint, disposeHandler: false);
        _clients.Add(http);
        ClientSecretCredentialOptions options = AzureCredentialFactory.ClientSecretOptions();
        options.Transport = new HttpClientTransport(http);
        options.Retry.MaxRetries = 0;
        return new ClientSecretCredential(tenantId, clientId, secret, options);
    }
}
