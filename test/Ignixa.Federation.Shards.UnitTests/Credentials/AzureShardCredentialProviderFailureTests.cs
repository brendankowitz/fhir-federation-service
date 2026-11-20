using Azure;
using Azure.Core;
using Azure.Identity;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Credentials;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Credentials;

/// <summary>Every failure is fail-closed and sanitized: a fixed message, no inner exception, nothing the SDK or the secret store said.</summary>
public sealed class AzureShardCredentialProviderFailureTests
{
    private const string LeakedSecret = "synthetic~Secret.Value-7QxK";
    private const string LeakedToken = "eyJsynthetic.leaked.payload";
    private const string LeakedTenant = "tenant-leaked-0f3a";
    private const string LeakedClient = "client-leaked-9c1d";
    private const string LeakedSecretName = "kv-secret-leaked";
    private const string LeakedVault = "https://kv-synthetic.vault.azure.net/secrets/kv-secret-leaked";

    private static readonly string[] Leaks = [LeakedSecret, LeakedToken, LeakedTenant, LeakedClient, LeakedSecretName, LeakedVault, "AADSTS7000215"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("api://synthetic-shard/.default")]
    [InlineData("api://synthetic-shard/.DEFAULT")]
    [InlineData("https://fhir.synthetic.example/.default")]
    [InlineData("api://synthetic shard")]
    [InlineData("api://synthetic\tshard")]
    [InlineData("api://synthetic\nshard")]
    [InlineData("api://synthetic-shard .default")]
    public async Task AMalformedOrPreScopedAudienceIsRefusedBeforeAnythingIsResolvedOrAcquired(string resource)
    {
        var secrets = new ScriptedSecretResolver("synthetic-secret-value");
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(secrets);

        ShardCredentialException managed = await Should.ThrowAsync<ShardCredentialException>(
            () => provider.GetAccessTokenAsync(SyntheticAuths.Managed(resource), Ct).AsTask());
        ShardCredentialException client = await Should.ThrowAsync<ShardCredentialException>(
            () => provider.GetAccessTokenAsync(SyntheticAuths.Client(resource), Ct).AsTask());

        managed.Failure.ShouldBe(ShardCredentialFailure.InvalidConfiguration);
        client.Failure.ShouldBe(ShardCredentialFailure.InvalidConfiguration);
        secrets.Requested.ShouldBeEmpty();
        sdk.CreatedCount.ShouldBe(0);
    }

    [Fact]
    public async Task ACanceledTokenStopsBeforeAnythingIsResolvedOrAcquired()
    {
        var secrets = new ScriptedSecretResolver("synthetic-secret-value");
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(secrets);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => provider.GetAccessTokenAsync(SyntheticAuths.Client("api://synthetic-shard"), canceled.Token).AsTask());

        secrets.Requested.ShouldBeEmpty();
        sdk.CreatedCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("authentication")]
    [InlineData("http")]
    [InlineData("io")]
    [InlineData("unauthorized")]
    public async Task AKnownSecretStoreFailureBecomesASanitizedSecretUnavailable(string kind)
    {
        var secrets = new ScriptedSecretResolver("unused") { Lookup = (_, _) => throw LeakyStoreFailure(kind) };
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(secrets);

        ShardCredentialException failure = await Should.ThrowAsync<ShardCredentialException>(
            () => provider.GetAccessTokenAsync(SyntheticAuths.Client("api://synthetic-shard"), Ct).AsTask());

        failure.Failure.ShouldBe(ShardCredentialFailure.SecretUnavailable);
        AssertSanitized(failure);
        sdk.CreatedCount.ShouldBe(0);
    }

    [Fact]
    public async Task AResolverThatReportsAMissingSecretWithTheTypedExceptionIsPassedThroughItsFixedMessage()
    {
        var secrets = new ScriptedSecretResolver("unused")
        {
            Lookup = (_, _) => throw new ShardCredentialException(ShardCredentialFailure.SecretUnavailable),
        };
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(secrets);

        ShardCredentialException failure = await Should.ThrowAsync<ShardCredentialException>(
            () => provider.GetAccessTokenAsync(SyntheticAuths.Client("api://synthetic-shard"), Ct).AsTask());

        failure.Failure.ShouldBe(ShardCredentialFailure.SecretUnavailable);
        AssertSanitized(failure);
        sdk.CreatedCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptySecretIsRefusedAsUnavailable(string secret)
    {
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(new ScriptedSecretResolver(secret));

        ShardCredentialException failure = await Should.ThrowAsync<ShardCredentialException>(
            () => provider.GetAccessTokenAsync(SyntheticAuths.Client("api://synthetic-shard"), Ct).AsTask());

        failure.Failure.ShouldBe(ShardCredentialFailure.SecretUnavailable);
        sdk.CreatedCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("authentication", true)]
    [InlineData("unavailable", true)]
    [InlineData("request", true)]
    [InlineData("authentication", false)]
    [InlineData("unavailable", false)]
    [InlineData("request", false)]
    public async Task AKnownSdkFailureBecomesASanitizedTokenUnavailable(string kind, bool managed)
    {
        var sdk = new RecordingCredentialFactory { OnCreate = c => c.Acquire = (_, _) => throw LeakySdkFailure(kind) };
        AzureShardCredentialProvider provider = sdk.Provider(new ScriptedSecretResolver(LeakedSecret));
        ShardAuth auth = managed
            ? SyntheticAuths.Managed("api://synthetic-shard", LeakedClient)
            : SyntheticAuths.Client("api://synthetic-shard", LeakedTenant, LeakedClient, LeakedSecretName);

        ShardCredentialException failure = await Should.ThrowAsync<ShardCredentialException>(() => provider.GetAccessTokenAsync(auth, Ct).AsTask());

        failure.Failure.ShouldBe(ShardCredentialFailure.TokenUnavailable);
        AssertSanitized(failure);
    }

    [Fact]
    public async Task ARejectedCredentialConstructionBecomesASanitizedInvalidConfiguration()
    {
        var provider = new AzureShardCredentialProvider(
            new ScriptedSecretResolver(LeakedSecret),
            _ => throw new ArgumentException($"Managed identity {LeakedClient} rejected."),
            (tenant, _, secret) => throw new ArgumentException($"Tenant '{tenant}' secret '{secret}' is invalid."));

        ShardCredentialException failure = await Should.ThrowAsync<ShardCredentialException>(
            () => provider.GetAccessTokenAsync(SyntheticAuths.Client("api://synthetic-shard", LeakedTenant), Ct).AsTask());
        ShardCredentialException managedFailure = await Should.ThrowAsync<ShardCredentialException>(
            () => provider.GetAccessTokenAsync(SyntheticAuths.Managed("api://synthetic-shard", LeakedClient), Ct).AsTask());

        failure.Failure.ShouldBe(ShardCredentialFailure.InvalidConfiguration);
        managedFailure.Failure.ShouldBe(ShardCredentialFailure.InvalidConfiguration);
        AssertSanitized(failure);
        AssertSanitized(managedFailure);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Bearer eyJsynthetic.header.payload")]
    [InlineData("eyJsynthetic header")]
    [InlineData("eyJsynthetic\r\nheader")]
    [InlineData("eyJsynthetic\theader")]
    public async Task AMissingOrMalformedTokenIsNotASuccessAndIsNeverQuoted(string token)
    {
        var sdk = new RecordingCredentialFactory
        {
            OnCreate = c => c.Acquire = (_, _) => ValueTask.FromResult(new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1))),
        };
        AzureShardCredentialProvider provider = sdk.Provider(new ScriptedSecretResolver("synthetic-secret-value"));

        ShardCredentialException failure = await Should.ThrowAsync<ShardCredentialException>(
            () => provider.GetAccessTokenAsync(SyntheticAuths.Managed("api://synthetic-shard"), Ct).AsTask());

        failure.Failure.ShouldBe(ShardCredentialFailure.InvalidToken);
        failure.Message.ShouldNotContain("eyJ");
    }

    [Fact]
    public async Task ADefaultAccessTokenIsNotASuccess()
    {
        var sdk = new RecordingCredentialFactory { OnCreate = c => c.Acquire = (_, _) => ValueTask.FromResult(default(AccessToken)) };
        AzureShardCredentialProvider provider = sdk.Provider(new ScriptedSecretResolver("synthetic-secret-value"));

        ShardCredentialException failure = await Should.ThrowAsync<ShardCredentialException>(
            () => provider.GetAccessTokenAsync(SyntheticAuths.Managed("api://synthetic-shard"), Ct).AsTask());

        failure.Failure.ShouldBe(ShardCredentialFailure.InvalidToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerCancellationInTheSdkSurfacesAsACleanCancellationForTheGivenToken(bool inSecretStore)
    {
        using var cts = new CancellationTokenSource();
        var secrets = new ScriptedSecretResolver("synthetic-secret-value");
        var sdk = new RecordingCredentialFactory();
        if (inSecretStore)
        {
            secrets.Lookup = async (_, token) =>
            {
                await cts.CancelAsync();
                throw new TaskCanceledException($"Lookup of {LeakedSecretName} returned {LeakedSecret}", new InvalidOperationException(LeakedToken), token);
            };
        }
        else
        {
            sdk.OnCreate = c => c.Acquire = async (_, token) =>
            {
                await cts.CancelAsync();
                throw new TaskCanceledException($"Token {LeakedToken} for {LeakedTenant}", new InvalidOperationException(LeakedSecret), token);
            };
        }

        AzureShardCredentialProvider provider = sdk.Provider(secrets);

        OperationCanceledException canceled = await Should.ThrowAsync<OperationCanceledException>(
            () => provider.GetAccessTokenAsync(SyntheticAuths.Client("api://synthetic-shard", LeakedTenant, LeakedClient, LeakedSecretName), cts.Token).AsTask());

        canceled.CancellationToken.ShouldBe(cts.Token);
        AssertSanitized(canceled);
    }

    [Fact]
    public async Task ATokenThatArrivesAfterCancellationIsNotReturned()
    {
        using var cts = new CancellationTokenSource();
        var sdk = new RecordingCredentialFactory
        {
            OnCreate = c => c.Acquire = async (_, _) =>
            {
                await cts.CancelAsync();
                return new AccessToken("synthetic.late.token", DateTimeOffset.UtcNow.AddHours(1));
            },
        };
        AzureShardCredentialProvider provider = sdk.Provider(new ScriptedSecretResolver("synthetic-secret-value"));

        OperationCanceledException canceled = await Should.ThrowAsync<OperationCanceledException>(
            () => provider.GetAccessTokenAsync(SyntheticAuths.Managed("api://synthetic-shard"), cts.Token).AsTask());

        canceled.CancellationToken.ShouldBe(cts.Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACancellationNotCausedByTheCallerIsASanitizedTimedOut(bool inSecretStore)
    {
        var secrets = new ScriptedSecretResolver("synthetic-secret-value");
        var sdk = new RecordingCredentialFactory();
        if (inSecretStore)
        {
            secrets.Lookup = (_, _) => throw new TaskCanceledException($"Lookup of {LeakedSecretName} timed out {LeakedSecret}");
        }
        else
        {
            sdk.OnCreate = c => c.Acquire = (_, _) => throw new OperationCanceledException($"Request to {LeakedVault} timed out {LeakedToken}");
        }

        AzureShardCredentialProvider provider = sdk.Provider(secrets);

        ShardCredentialException failure = await Should.ThrowAsync<ShardCredentialException>(
            () => provider.GetAccessTokenAsync(SyntheticAuths.Client("api://synthetic-shard", LeakedTenant, LeakedClient, LeakedSecretName), Ct).AsTask());

        failure.Failure.ShouldBe(ShardCredentialFailure.TimedOut);
        AssertSanitized(failure);
    }

    [Fact]
    public void EachFailureHasItsOwnFixedMessageAndNoInnerException()
    {
        ShardCredentialException[] all = [.. Enum.GetValues<ShardCredentialFailure>().Select(f => new ShardCredentialException(f))];

        all.Select(e => e.Message).Distinct().Count().ShouldBe(all.Length);
        all.ShouldAllBe(e => e.InnerException == null && e.Data.Count == 0);
        Should.Throw<ArgumentOutOfRangeException>(() => new ShardCredentialException((ShardCredentialFailure)999));
    }

    private static void AssertSanitized(Exception failure)
    {
        failure.InnerException.ShouldBeNull();
        failure.Data.Count.ShouldBe(0);
        foreach (string leak in Leaks)
        {
            failure.Message.ShouldNotContain(leak);
            failure.ToString().ShouldNotContain(leak);
        }
    }

    private static Exception LeakyStoreFailure(string kind) => kind switch
    {
        "request" => new RequestFailedException(403, $"Caller is not authorized to get {LeakedVault}. Bearer {LeakedToken}", new InvalidOperationException(LeakedSecret)),
        "authentication" => new AuthenticationFailedException($"AADSTS7000215: secret {LeakedSecret} for client {LeakedClient} in {LeakedTenant}", new InvalidOperationException(LeakedToken)),
        "http" => new HttpRequestException($"No such host: {LeakedVault}", new InvalidOperationException(LeakedSecret)),
        "io" => new IOException($"Could not read /mnt/secrets/{LeakedSecretName}: {LeakedSecret}"),
        "unauthorized" => new UnauthorizedAccessException($"Access to {LeakedSecretName} denied for {LeakedClient}"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static Exception LeakySdkFailure(string kind) => kind switch
    {
        "authentication" => new AuthenticationFailedException(
            $"AADSTS7000215: Invalid client secret '{LeakedSecret}' for application {LeakedClient} in tenant {LeakedTenant}. Token {LeakedToken}",
            new InvalidOperationException($"client_secret={LeakedSecret}")),
        "unavailable" => new CredentialUnavailableException($"ManagedIdentityCredential authentication unavailable for {LeakedClient}: {LeakedToken}", new InvalidOperationException(LeakedSecret)),
        "request" => new RequestFailedException(401, $"Service request failed. Status: 401\nContent: {{\"error\":\"{LeakedSecret}\",\"access_token\":\"{LeakedToken}\"}}", new InvalidOperationException(LeakedTenant)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
