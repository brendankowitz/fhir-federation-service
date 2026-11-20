using Azure.Core;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Credentials;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Credentials;

/// <summary>Which credential, tenant, client, secret and single scope reach the SDK for each published shard auth.</summary>
public sealed class AzureShardCredentialProviderSelectionTests
{
    private const string UserAssignedClientId = "11111111-2222-3333-4444-555555555555";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SystemAssignedIdentity_IsSelectedWithoutAClientIdAndNeverTouchesTheSecretStore()
    {
        var secrets = new ScriptedSecretResolver("synthetic-secret-value");
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(secrets);

        string token = await provider.GetAccessTokenAsync(SyntheticAuths.Managed("api://synthetic-shard"), Ct);

        CreatedManagedAssert(sdk, expectedClientId: null).Credential.Token.ShouldBe(token);
        sdk.Clients.ShouldBeEmpty();
        secrets.Requested.ShouldBeEmpty();
    }

    [Fact]
    public async Task UserAssignedIdentity_IsSelectedByItsExactClientId()
    {
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(new ScriptedSecretResolver("unused"));

        string token = await provider.GetAccessTokenAsync(SyntheticAuths.Managed("api://synthetic-shard", UserAssignedClientId), Ct);

        CreatedManagedAssert(sdk, UserAssignedClientId).Credential.Token.ShouldBe(token);
    }

    [Fact]
    public async Task InterleavedIdentitiesAndAudiences_NeverShareACredentialOrBleedAScope()
    {
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(new ScriptedSecretResolver("unused"));
        ManagedIdentityAuth systemA = SyntheticAuths.Managed("api://audience-a");
        ManagedIdentityAuth userB = SyntheticAuths.Managed("api://audience-b", UserAssignedClientId);
        ManagedIdentityAuth systemC = SyntheticAuths.Managed("api://audience-c");

        string first = await provider.GetAccessTokenAsync(systemA, Ct);
        string second = await provider.GetAccessTokenAsync(userB, Ct);
        string third = await provider.GetAccessTokenAsync(systemC, Ct);

        IReadOnlyList<RecordingCredentialFactory.CreatedManaged> created = sdk.Managed;
        created.Select(c => c.ClientId).ShouldBe([null, UserAssignedClientId]);
        RecordingTokenCredential system = created[0].Credential;
        RecordingTokenCredential user = created[1].Credential;
        first.ShouldBe(system.Token);
        third.ShouldBe(system.Token);
        second.ShouldBe(user.Token);
        system.Token.ShouldNotBe(user.Token);
        system.ScopeRequests.ShouldBe([["api://audience-a/.default"], ["api://audience-c/.default"]]);
        user.ScopeRequests.ShouldBe([["api://audience-b/.default"]]);
    }

    [Fact]
    public async Task ClientCredentials_BindTenantClientSecretNameResolvedValueAndOneScope()
    {
        var secrets = new ScriptedSecretResolver("synthetic-secret-value-1");
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(secrets);

        string token = await provider.GetAccessTokenAsync(
            SyntheticAuths.Client("api://synthetic-shard", "tenant-east", "client-east", "kv-secret-east"), Ct);

        RecordingCredentialFactory.CreatedClient created = sdk.Clients.ShouldHaveSingleItem();
        created.TenantId.ShouldBe("tenant-east");
        created.ClientId.ShouldBe("client-east");
        created.Secret.ShouldBe("synthetic-secret-value-1");
        created.Credential.ScopeRequests.ShouldBe([["api://synthetic-shard/.default"]]);
        created.Credential.Token.ShouldBe(token);
        secrets.Requested.ShouldBe(["kv-secret-east"]);
        sdk.Managed.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConcurrentClientCredentialsForDifferentShards_KeepTheirOwnBindings()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secrets = new ScriptedSecretResolver("unused")
        {
            Lookup = async (name, _) =>
            {
                await gate.Task;
                return $"value-of-{name}";
            },
        };
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(secrets);

        Task<string> east = provider.GetAccessTokenAsync(SyntheticAuths.Client("api://east", "tenant-e", "client-e", "secret-e"), Ct).AsTask();
        Task<string> west = provider.GetAccessTokenAsync(SyntheticAuths.Client("https://west.synthetic.example/", "tenant-w", "client-w", "secret-w"), Ct).AsTask();
        gate.SetResult();
        string[] tokens = await Task.WhenAll(east, west);

        RecordingCredentialFactory.CreatedClient eastCredential = sdk.Clients.Single(c => c.TenantId == "tenant-e");
        RecordingCredentialFactory.CreatedClient westCredential = sdk.Clients.Single(c => c.TenantId == "tenant-w");
        (eastCredential.ClientId, eastCredential.Secret).ShouldBe(("client-e", "value-of-secret-e"));
        (westCredential.ClientId, westCredential.Secret).ShouldBe(("client-w", "value-of-secret-w"));
        eastCredential.Credential.ScopeRequests.ShouldBe([["api://east/.default"]]);
        westCredential.Credential.ScopeRequests.ShouldBe([["https://west.synthetic.example//.default"]]);
        tokens[0].ShouldBe(eastCredential.Credential.Token);
        tokens[1].ShouldBe(westCredential.Credential.Token);
    }

    [Theory]
    [InlineData("api://synthetic-shard", "api://synthetic-shard/.default")]
    [InlineData("https://fhir.synthetic.example", "https://fhir.synthetic.example/.default")]
    [InlineData("https://fhir.synthetic.example/", "https://fhir.synthetic.example//.default")]
    [InlineData("https://fhir.synthetic.example/audience", "https://fhir.synthetic.example/audience/.default")]
    public async Task TheAudienceBecomesExactlyOneDefaultScopeKeepingItsSpelling(string resource, string expectedScope)
    {
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(new ScriptedSecretResolver("synthetic-secret-value"));

        await provider.GetAccessTokenAsync(SyntheticAuths.Managed(resource), Ct);
        await provider.GetAccessTokenAsync(SyntheticAuths.Client(resource), Ct);

        sdk.Managed.Single().Credential.ScopeRequests.ShouldBe([[expectedScope]]);
        sdk.Clients.Single().Credential.ScopeRequests.ShouldBe([[expectedScope]]);
    }

    [Fact]
    public async Task ARotatedSecretIsUsedByTheNextAcquisitionWhileAnOlderOneIsStillBlocked()
    {
        var secrets = new ScriptedSecretResolver("secret-v1");
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sdk = new RecordingCredentialFactory();
        sdk.OnCreate = credential =>
        {
            if (sdk.CreatedCount == 1)
            {
                credential.Acquire = async (_, _) =>
                {
                    await releaseFirst.Task;
                    return new AccessToken(credential.Token, DateTimeOffset.UtcNow.AddHours(1));
                };
            }
        };
        AzureShardCredentialProvider provider = sdk.Provider(secrets);
        ClientCredentialsAuth auth = SyntheticAuths.Client("api://synthetic-shard");

        Task<string> older = provider.GetAccessTokenAsync(auth, Ct).AsTask();
        secrets.Value = "secret-v2";
        string newer = await provider.GetAccessTokenAsync(auth, Ct);
        older.IsCompleted.ShouldBeFalse();
        releaseFirst.SetResult();
        string olderToken = await older;

        sdk.Clients.Select(c => c.Secret).ShouldBe(["secret-v1", "secret-v2"]);
        newer.ShouldBe(sdk.Clients[1].Credential.Token);
        olderToken.ShouldBe(sdk.Clients[0].Credential.Token);
        secrets.Requested.ShouldBe(["secret-1", "secret-1"]);
    }

    [Fact]
    public async Task ASecretThatDisappearsAfterASuccessFailsClosedWithoutReusingTheOldSecretOrToken()
    {
        var secrets = new ScriptedSecretResolver("secret-v1");
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(secrets);
        ClientCredentialsAuth auth = SyntheticAuths.Client("api://synthetic-shard");
        await provider.GetAccessTokenAsync(auth, Ct);
        secrets.Lookup = (_, _) => throw new ShardCredentialException(ShardCredentialFailure.SecretUnavailable);

        ShardCredentialException failure = await Should.ThrowAsync<ShardCredentialException>(() => provider.GetAccessTokenAsync(auth, Ct).AsTask());

        failure.Failure.ShouldBe(ShardCredentialFailure.SecretUnavailable);
        sdk.CreatedCount.ShouldBe(1);
        sdk.Clients.Single().Credential.ScopeRequests.Count.ShouldBe(1);
    }

    [Fact]
    public void ThePublicConstructorRequiresASecretResolver()
    {
        Should.Throw<ArgumentNullException>(() => new AzureShardCredentialProvider(null!));
    }

    [Fact]
    public async Task ANullAuthIsRefused()
    {
        AzureShardCredentialProvider provider = new RecordingCredentialFactory().Provider(new ScriptedSecretResolver("unused"));

        await Should.ThrowAsync<ArgumentNullException>(() => provider.GetAccessTokenAsync(null!, Ct).AsTask());
    }

    private static RecordingCredentialFactory.CreatedManaged CreatedManagedAssert(RecordingCredentialFactory sdk, string? expectedClientId)
    {
        RecordingCredentialFactory.CreatedManaged created = sdk.Managed.ShouldHaveSingleItem();
        created.ClientId.ShouldBe(expectedClientId);
        created.Credential.ScopeRequests.ShouldBe([["api://synthetic-shard/.default"]]);
        return created;
    }
}
