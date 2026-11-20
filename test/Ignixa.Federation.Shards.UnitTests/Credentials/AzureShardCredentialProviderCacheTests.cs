using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Credentials;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Credentials;

/// <summary>
/// Which client-secret credential the provider keeps and selects across acquisitions: one current credential per tenant,
/// client and secret name, replaced only by a newer lookup of a different value, and never used by a failed lookup.
/// </summary>
public sealed class AzureShardCredentialProviderCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SequentialLookupsOfAnUnchangedSecretReuseOneCredentialAndStillResolveEachTime()
    {
        var secrets = new ScriptedSecretResolver("secret-v1");
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(secrets);
        ClientCredentialsAuth auth = SyntheticAuths.Client("api://synthetic-shard");

        string first = await provider.GetAccessTokenAsync(auth, Ct);
        string second = await provider.GetAccessTokenAsync(auth, Ct);

        RecordingCredentialFactory.CreatedClient created = sdk.Clients.ShouldHaveSingleItem();
        created.Credential.ScopeRequests.Count.ShouldBe(2);
        first.ShouldBe(created.Credential.Token);
        second.ShouldBe(created.Credential.Token);
        secrets.Requested.ShouldBe(["secret-1", "secret-1"]);
    }

    [Fact]
    public async Task ConcurrentLookupsOfAnUnchangedSecretShareExactlyOneCredential()
    {
        const int Callers = 16;
        int started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secrets = new ScriptedSecretResolver("unused")
        {
            Lookup = async (_, _) =>
            {
                if (Interlocked.Increment(ref started) == Callers)
                {
                    allStarted.SetResult();
                }

                await release.Task;
                return "secret-v1";
            },
        };
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(secrets);
        ClientCredentialsAuth auth = SyntheticAuths.Client("api://synthetic-shard");

        Task<string>[] calls = [.. Enumerable.Range(0, Callers).Select(_ => provider.GetAccessTokenAsync(auth, Ct).AsTask())];
        await allStarted.Task.WaitAsync(Ct);
        calls.ShouldAllBe(call => !call.IsCompleted);
        release.SetResult();
        string[] tokens = await Task.WhenAll(calls);

        RecordingCredentialFactory.CreatedClient created = sdk.Clients.ShouldHaveSingleItem();
        created.Credential.ScopeRequests.Count.ShouldBe(Callers);
        tokens.ShouldAllBe(token => token == created.Credential.Token);
    }

    [Fact]
    public async Task TwoSecretNamesForOneTenantAndClientKeepSeparateCredentialsEvenWithEqualValues()
    {
        var secrets = new ScriptedSecretResolver("same-secret-value");
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(secrets);
        ClientCredentialsAuth primary = SyntheticAuths.Client("api://synthetic-shard", "tenant-1", "client-1", "secret-primary");
        ClientCredentialsAuth secondary = SyntheticAuths.Client("api://synthetic-shard", "tenant-1", "client-1", "secret-secondary");

        string p1 = await provider.GetAccessTokenAsync(primary, Ct);
        string s1 = await provider.GetAccessTokenAsync(secondary, Ct);
        string p2 = await provider.GetAccessTokenAsync(primary, Ct);
        string s2 = await provider.GetAccessTokenAsync(secondary, Ct);

        sdk.Clients.Count.ShouldBe(2);
        p1.ShouldBe(sdk.Clients[0].Credential.Token);
        s1.ShouldBe(sdk.Clients[1].Credential.Token);
        p2.ShouldBe(p1);
        s2.ShouldBe(s1);
        secrets.Requested.ShouldBe(["secret-primary", "secret-secondary", "secret-primary", "secret-secondary"]);
    }

    [Fact]
    public async Task InterleavedTenantsClientsAndAudiencesNeverBleedIntoAnotherCachedCredential()
    {
        var secrets = new ScriptedSecretResolver("unused") { Lookup = (name, _) => ValueTask.FromResult($"value-of-{name}") };
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(secrets);
        ClientCredentialsAuth east = SyntheticAuths.Client("api://east", "tenant-e", "client-e", "secret-e");
        ClientCredentialsAuth west = SyntheticAuths.Client("api://west", "tenant-w", "client-w", "secret-w");
        ClientCredentialsAuth otherClient = SyntheticAuths.Client("api://east", "tenant-e", "client-x", "secret-e");

        Task<string>[] calls = [.. Enumerable.Range(0, 4).SelectMany(_ => new[] { east, west, otherClient })
            .Select(auth => provider.GetAccessTokenAsync(auth, Ct).AsTask())];
        await Task.WhenAll(calls);

        sdk.Clients.Select(c => (c.TenantId, c.ClientId, c.Secret)).Order().ShouldBe([
            ("tenant-e", "client-e", "value-of-secret-e"),
            ("tenant-e", "client-x", "value-of-secret-e"),
            ("tenant-w", "client-w", "value-of-secret-w"),
        ]);
        sdk.Clients.Single(c => c.ClientId == "client-e").Credential.ScopeRequests.ShouldAllBe(s => s.Single() == "api://east/.default");
        sdk.Clients.Single(c => c.ClientId == "client-x").Credential.ScopeRequests.ShouldAllBe(s => s.Single() == "api://east/.default");
        sdk.Clients.Single(c => c.ClientId == "client-w").Credential.ScopeRequests.ShouldAllBe(s => s.Single() == "api://west/.default");
        sdk.Clients.ShouldAllBe(c => c.Credential.ScopeRequests.Count == 4);
    }

    [Fact]
    public async Task AnOlderLookupThatReturnsAnOlderValueAfterANewerOneWasAcceptedFailsClosedWithoutRollingBack()
    {
        var olderLookupMayReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var olderLookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int lookups = 0;
        var secrets = new ScriptedSecretResolver("unused")
        {
            Lookup = async (_, _) =>
            {
                switch (Interlocked.Increment(ref lookups))
                {
                    case 1:
                        olderLookupStarted.SetResult();
                        await olderLookupMayReturn.Task;
                        return "secret-v1";
                    default:
                        return "secret-v2";
                }
            },
        };
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(secrets);
        ClientCredentialsAuth auth = SyntheticAuths.Client("api://synthetic-shard");

        Task<string> older = provider.GetAccessTokenAsync(auth, Ct).AsTask();
        await olderLookupStarted.Task.WaitAsync(Ct);
        string newer = await provider.GetAccessTokenAsync(auth, Ct);
        olderLookupMayReturn.SetResult();
        ShardCredentialException stale = await Should.ThrowAsync<ShardCredentialException>(older);
        string afterwards = await provider.GetAccessTokenAsync(auth, Ct);

        stale.Failure.ShouldBe(ShardCredentialFailure.SecretUnavailable);
        RecordingCredentialFactory.CreatedClient v2 = sdk.Clients.ShouldHaveSingleItem();
        v2.Secret.ShouldBe("secret-v2");
        newer.ShouldBe(v2.Credential.Token);
        afterwards.ShouldBe(v2.Credential.Token);
        v2.Credential.ScopeRequests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AnOlderLookupThatReturnsTheAcceptedValueLateReusesTheCurrentCredential()
    {
        var olderLookupMayReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var olderLookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int lookups = 0;
        var secrets = new ScriptedSecretResolver("unused")
        {
            Lookup = async (_, _) =>
            {
                if (Interlocked.Increment(ref lookups) == 1)
                {
                    olderLookupStarted.SetResult();
                    await olderLookupMayReturn.Task;
                }

                return "secret-v2";
            },
        };
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(secrets);
        ClientCredentialsAuth auth = SyntheticAuths.Client("api://synthetic-shard");

        Task<string> older = provider.GetAccessTokenAsync(auth, Ct).AsTask();
        await olderLookupStarted.Task.WaitAsync(Ct);
        string newer = await provider.GetAccessTokenAsync(auth, Ct);
        olderLookupMayReturn.SetResult();
        string olderToken = await older;

        RecordingCredentialFactory.CreatedClient created = sdk.Clients.ShouldHaveSingleItem();
        olderToken.ShouldBe(created.Credential.Token);
        newer.ShouldBe(created.Credential.Token);
    }

    [Fact]
    public async Task ADifferentValueFromALookupOlderThanAReconfirmationOfTheCurrentValueIsStale()
    {
        var olderLookupMayReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var olderLookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int lookups = 0;
        var secrets = new ScriptedSecretResolver("unused")
        {
            Lookup = async (_, _) =>
            {
                if (Interlocked.Increment(ref lookups) == 2)
                {
                    olderLookupStarted.SetResult();
                    await olderLookupMayReturn.Task;
                    return "secret-v0";
                }

                return "secret-v1";
            },
        };
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(secrets);
        ClientCredentialsAuth auth = SyntheticAuths.Client("api://synthetic-shard");
        await provider.GetAccessTokenAsync(auth, Ct);

        Task<string> older = provider.GetAccessTokenAsync(auth, Ct).AsTask();
        await olderLookupStarted.Task.WaitAsync(Ct);
        await provider.GetAccessTokenAsync(auth, Ct);
        olderLookupMayReturn.SetResult();
        ShardCredentialException stale = await Should.ThrowAsync<ShardCredentialException>(older);

        stale.Failure.ShouldBe(ShardCredentialFailure.SecretUnavailable);
        RecordingCredentialFactory.CreatedClient current = sdk.Clients.ShouldHaveSingleItem();
        current.Secret.ShouldBe("secret-v1");
        current.Credential.ScopeRequests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AnOlderValueSeenAgainByANewerLookupIsANewRotationNotAStaleOne()
    {
        var secrets = new ScriptedSecretResolver("secret-v1");
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(secrets);
        ClientCredentialsAuth auth = SyntheticAuths.Client("api://synthetic-shard");

        await provider.GetAccessTokenAsync(auth, Ct);
        secrets.Value = "secret-v2";
        await provider.GetAccessTokenAsync(auth, Ct);
        secrets.Value = "secret-v1";
        string rolledBackByTheStore = await provider.GetAccessTokenAsync(auth, Ct);

        sdk.Clients.Select(c => c.Secret).ShouldBe(["secret-v1", "secret-v2", "secret-v1"]);
        rolledBackByTheStore.ShouldBe(sdk.Clients[2].Credential.Token);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("blank")]
    [InlineData("store-failure")]
    public async Task AFailedLookupWithAPopulatedCacheFailsClosedWithoutUsingTheCachedCredential(string failure)
    {
        var secrets = new ScriptedSecretResolver("secret-v1");
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(secrets);
        ClientCredentialsAuth auth = SyntheticAuths.Client("api://synthetic-shard");
        await provider.GetAccessTokenAsync(auth, Ct);
        secrets.Lookup = failure switch
        {
            "missing" => (_, _) => throw new ShardCredentialException(ShardCredentialFailure.SecretUnavailable),
            "blank" => (_, _) => ValueTask.FromResult(" "),
            _ => (_, _) => throw new IOException("synthetic store failure"),
        };

        ShardCredentialException thrown = await Should.ThrowAsync<ShardCredentialException>(() => provider.GetAccessTokenAsync(auth, Ct).AsTask());

        thrown.Failure.ShouldBe(ShardCredentialFailure.SecretUnavailable);
        RecordingCredentialFactory.CreatedClient cached = sdk.Clients.ShouldHaveSingleItem();
        cached.Credential.ScopeRequests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("secret-v1")]
    [InlineData("secret-v2")]
    public async Task ACanceledLookupWithAPopulatedCacheNeitherUsesNorReplacesTheCachedCredential(string lateValue)
    {
        var secrets = new ScriptedSecretResolver("secret-v1");
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(secrets);
        ClientCredentialsAuth auth = SyntheticAuths.Client("api://synthetic-shard");
        await provider.GetAccessTokenAsync(auth, Ct);
        using var cts = new CancellationTokenSource();
        secrets.Lookup = async (_, _) =>
        {
            await cts.CancelAsync();
            return lateValue;
        };

        OperationCanceledException canceled = await Should.ThrowAsync<OperationCanceledException>(
            () => provider.GetAccessTokenAsync(auth, cts.Token).AsTask());
        secrets.Lookup = (_, _) => ValueTask.FromResult("secret-v1");
        string afterwards = await provider.GetAccessTokenAsync(auth, Ct);

        canceled.CancellationToken.ShouldBe(cts.Token);
        RecordingCredentialFactory.CreatedClient cached = sdk.Clients.ShouldHaveSingleItem();
        cached.Secret.ShouldBe("secret-v1");
        cached.Credential.ScopeRequests.Count.ShouldBe(2);
        afterwards.ShouldBe(cached.Credential.Token);
    }

    [Fact]
    public async Task ARejectedReplacementLeavesTheCurrentCredentialInPlace()
    {
        var secrets = new ScriptedSecretResolver("secret-v1");
        var sdk = new RecordingCredentialFactory();
        var provider = new AzureShardCredentialProvider(
            secrets,
            sdk.CreateManaged,
            (tenant, client, secret) => secret == "secret-v1" ? sdk.CreateClient(tenant, client, secret) : throw new ArgumentException("synthetic rejection"));
        ClientCredentialsAuth auth = SyntheticAuths.Client("api://synthetic-shard");
        await provider.GetAccessTokenAsync(auth, Ct);
        secrets.Value = "secret-v2";

        ShardCredentialException rejected = await Should.ThrowAsync<ShardCredentialException>(() => provider.GetAccessTokenAsync(auth, Ct).AsTask());
        secrets.Value = "secret-v1";
        string afterwards = await provider.GetAccessTokenAsync(auth, Ct);

        rejected.Failure.ShouldBe(ShardCredentialFailure.InvalidConfiguration);
        RecordingCredentialFactory.CreatedClient cached = sdk.Clients.ShouldHaveSingleItem();
        afterwards.ShouldBe(cached.Credential.Token);
        cached.Credential.ScopeRequests.Count.ShouldBe(2);
    }
}
