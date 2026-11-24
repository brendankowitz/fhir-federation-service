using Ignixa.Federation.Shards.Credentials;
using Ignixa.Federation.Shards.Http;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// A typed credential failure at the metadata exchange (design spec §9.1, §10.2), observed at a real TLS loopback server:
/// it is a local, classified, sanitized failure of the advertisement fetch only, and a caller's cancellation still wins.
/// </summary>
public sealed class HttpShardClientMetadataCredentialTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ShardCredentialFailure> CredentialFailures => new(Enum.GetValues<ShardCredentialFailure>());

    [Theory]
    [MemberData(nameof(CredentialFailures))]
    public async Task GetCapabilities_ReportsACredentialFailureAsALocalSanitizedFailureAndSendsNothing(ShardCredentialFailure cause)
    {
        await using var shard = ShardHarness.Start();
        shard.Credentials.Acquire = _ => ValueTask.FromException<string>(new ShardCredentialException(cause));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.GetCapabilitiesAsync(Ct));

        failure.Failure.ShouldBe(ShardCallFailure.CredentialUnavailable);
        failure.Shard.ShouldBe(shard.Shard.Id);
        failure.StatusCode.ShouldBeNull();
        failure.Fault.ShouldBe(ShardFault.None, "an unsent local failure is not a wire fault");
        failure.InnerException.ShouldBeNull("the credential exception must not be chained into diagnostics");
        failure.Message.ShouldNotContain(cause.ToString());
        failure.Message.ShouldNotContain("identity platform");
        failure.Message.ShouldNotContain("secret", Case.Insensitive);
        shard.Credentials.Calls.ShouldBe(1);
        shard.Meter.Sends.ShouldBeEmpty();
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetCapabilities_KeepsACallerCancellationAsCancellationEvenIfTheProviderReportsACredentialFailure()
    {
        await using var shard = ShardHarness.Start();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task cancellation = Task.CompletedTask;
        shard.Credentials.Acquire = _ =>
        {
            // Throw before the transport starts waiting, so the cancellation cannot win in WaitAsync instead.
            cancellation = caller.CancelAsync();
            throw new ShardCredentialException(ShardCredentialFailure.TokenUnavailable);
        };

        OperationCanceledException cancelled =
            await Should.ThrowAsync<OperationCanceledException>(() => shard.Client.GetCapabilitiesAsync(caller.Token));
        await cancellation;

        cancelled.CancellationToken.ShouldBe(caller.Token);
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchKeepsTheCredentialProvidersOwnException()
    {
        await using var shard = ShardHarness.Start();
        var outage = new ShardCredentialException(ShardCredentialFailure.TokenUnavailable);
        shard.Credentials.Acquire = _ => ValueTask.FromException<string>(outage);

        (await Should.ThrowAsync<ShardCredentialException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct))).ShouldBeSameAs(outage);

        shard.Server.Requests.ShouldBeEmpty();
    }
}
