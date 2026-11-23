using Ignixa.Federation.Api.Budgets;
using Ignixa.Federation.Api.Reads;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Tests.Integration.Budgets;
using Ignixa.Federation.Tests.Integration.Search;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Reads;

/// <summary>How a proxied read fails, and that a stop (cancellation, deadline, cost) outranks any backend outcome before anything is written.</summary>
public sealed class ProxiedReadFailureTests
{
    private const string Dup = "/shards/k7q2/Patient/dup";
    private static readonly ShardId East = ShardId.Parse("east");

    private static ScriptedReadBinding Returning(Func<ReadCall, ShardReadResult> script) => new() { Script = script };

    [Fact]
    public async Task NotFound_And_Gone_AreDistinct()
    {
        var binding = Returning(call => call.Key.Id.Value == "deleted" ? ShardReadResult.Gone : ShardReadResult.NotFound);
        using var host = ProxiedReadTests.Start(binding);

        ReadResponse missing = await ReadResponse.GetAsync(host, "/shards/k7q2/Patient/missing", host.Token());
        ReadResponse gone = await ReadResponse.GetAsync(host, "/shards/k7q2/Patient/deleted", host.Token());

        missing.Status.ShouldBe(404);
        missing.IssueCode.ShouldBe("not-found");
        gone.Status.ShouldBe(410);
        gone.IssueCode.ShouldBe("deleted");
        missing.IsCleanFailure.ShouldBeTrue();
        gone.IsCleanFailure.ShouldBeTrue();
        missing.Body.ShouldNotContain("k7q2");
        gone.Body.ShouldNotContain("east");
    }

    [Theory]
    [InlineData(ShardFailureReason.Timeout, 504, "timeout")]
    [InlineData(ShardFailureReason.Failed, 502, "exception")]
    [InlineData(ShardFailureReason.ContractViolation, 502, "exception")]
    public async Task NeededShardFailure_IsGatewayFailure_NamingOnlyTheOwnerAlias(ShardFailureReason reason, int status, string issue)
    {
        var binding = Returning(_ => throw new ShardFailureException(East, reason, new InvalidOperationException("secret https://fhir-east.example.org/Patient/dup token=abc")));
        using var host = ProxiedReadTests.Start(binding);

        ReadResponse result = await ReadResponse.GetAsync(host, Dup, host.Token());

        result.Status.ShouldBe(status);
        result.IssueCode.ShouldBe(issue);
        result.IsCleanFailure.ShouldBeTrue();
        result.Body.ShouldContain("k7q2");
        result.Body.ShouldNotContain("secret");
        result.Body.ShouldNotContain("fhir-east");
        result.Body.ShouldNotContain("token=abc");
        result.Body.ShouldNotContain("east\"");
    }

    [Fact]
    public async Task CircuitOpen_IsUnavailable_ForThisReadOnly()
    {
        var binding = Returning(_ => throw new ShardReadCircuitOpenException(East));
        using var host = ProxiedReadTests.Start(binding);

        ReadResponse result = await ReadResponse.GetAsync(host, Dup, host.Token());
        SearchResult search = await host.GetAsync("/Patient?name=Orchid");

        result.Status.ShouldBe(503);
        result.IssueCode.ShouldBe("transient");
        result.IsCleanFailure.ShouldBeTrue();
        result.Body.ShouldContain("k7q2");
        search.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnyOtherBindingFailure_IsASanitizedBadGateway()
    {
        var binding = Returning(_ => throw new InvalidOperationException("backend said: https://fhir-east.example.org/secret"));
        using var host = ProxiedReadTests.Start(binding);

        ReadResponse result = await ReadResponse.GetAsync(host, Dup, host.Token());

        result.Status.ShouldBe(502);
        result.IssueCode.ShouldBe("exception");
        result.IsCleanFailure.ShouldBeTrue();
        result.Body.ShouldNotContain("secret");
        result.Body.ShouldNotContain("fhir-east");
    }

    [Fact]
    public async Task ABackendCallTimeout_ThatIsNotTheRequestsOwn_IsAGatewayTimeout()
    {
        using var unrelated = new CancellationTokenSource();
        await unrelated.CancelAsync();
        var binding = Returning(_ => throw new OperationCanceledException(unrelated.Token));
        using var host = ProxiedReadTests.Start(binding);

        ReadResponse result = await ReadResponse.GetAsync(host, Dup, host.Token());

        result.Status.ShouldBe(504);
        result.IssueCode.ShouldBe("timeout");
        result.IsCleanFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task AKeyThatIsNotTheRequestedOne_IsAContractViolation_NotServed()
    {
        var binding = Returning(call => ShardReadResult.Found(new ShardEntry(
            new Ignixa.Federation.Core.Identity.ResourceKey(ShardId.Parse("west"), call.Key.Type, call.Key.Id), null, null, SearchWorld.Slice("west", "Patient", "dup"))));
        using var host = ProxiedReadTests.Start(binding);

        ReadResponse result = await ReadResponse.GetAsync(host, Dup, host.Token());

        result.Status.ShouldBe(502);
        result.IsCleanFailure.ShouldBeTrue();
        result.Body.ShouldNotContain("Patient\"");
        result.Body.ShouldNotContain("fhir-west");
    }

    [Theory]
    [InlineData("id")]
    [InlineData("type")]
    public async Task AKeyWithAnotherIdOrType_IsAContractViolation(string differs)
    {
        var binding = Returning(call => ShardReadResult.Found(new ShardEntry(
            new Ignixa.Federation.Core.Identity.ResourceKey(
                call.Key.Shard,
                differs == "type" ? Ignixa.Federation.Core.Identity.ResourceType.Parse("Observation") : call.Key.Type,
                differs == "id" ? Ignixa.Federation.Core.Identity.ResourceId.Parse("other") : call.Key.Id),
            null, null, SearchWorld.Slice("east", "Patient", "dup"))));
        using var host = ProxiedReadTests.Start(binding);

        ReadResponse result = await ReadResponse.GetAsync(host, Dup, host.Token());

        result.Status.ShouldBe(502);
        result.IsCleanFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task ANullResult_IsAContractViolation()
    {
        var binding = Returning(_ => null!);
        using var host = ProxiedReadTests.Start(binding);

        ReadResponse result = await ReadResponse.GetAsync(host, Dup, host.Token());

        result.Status.ShouldBe(502);
        result.IsCleanFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task CallerCancellation_OutranksASuccessfulRead_AndWritesNothing()
    {
        using var caller = new CancellationTokenSource();
        var binding = Returning(call =>
        {
            ShardReadResult found = ScriptedReadBinding.Corpus(call);
            caller.Cancel();
            return found;
        });
        using var host = ProxiedReadTests.Start(binding);

        ReadResponse result = await ReadResponse.GetRawAsync(host, Dup, host.Token(), caller.Token);

        result.Status.ShouldBe(499);
        result.ContentLocation.ShouldBeNull();
        result.Bytes.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ShardFailureReason.Failed)]
    [InlineData(ShardFailureReason.Timeout)]
    public async Task CallerCancellation_OutranksAWrappedBackendFailure(ShardFailureReason reason)
    {
        using var caller = new CancellationTokenSource();
        var binding = Returning(_ =>
        {
            caller.Cancel();
            throw new ShardFailureException(East, reason, new OperationCanceledException(caller.Token));
        });
        using var host = ProxiedReadTests.Start(binding);

        ReadResponse result = await ReadResponse.GetRawAsync(host, Dup, host.Token(), caller.Token);

        result.Status.ShouldBe(499);
        result.Bytes.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallerCancellation_OutranksANotFoundAndACircuitOpen()
    {
        using var caller = new CancellationTokenSource();
        int call = 0;
        var binding = Returning(_ =>
        {
            caller.Cancel();
            return ++call == 1 ? ShardReadResult.NotFound : throw new ShardReadCircuitOpenException(East);
        });
        using var host = ProxiedReadTests.Start(binding);

        ReadResponse notFound = await ReadResponse.GetRawAsync(host, Dup, host.Token(), caller.Token);
        using var second = new CancellationTokenSource();
        binding.Script = _ =>
        {
            second.Cancel();
            throw new ShardReadCircuitOpenException(East);
        };
        ReadResponse open = await ReadResponse.GetRawAsync(host, Dup, host.Token(), second.Token);

        notFound.Status.ShouldBe(499);
        open.Status.ShouldBe(499);
    }

    [Theory]
    [InlineData("found")]
    [InlineData("gone")]
    [InlineData("notfound")]
    [InlineData("failed")]
    [InlineData("circuit")]
    public async Task AnExpiredDeadline_OutranksEveryBackendOutcome(string outcome)
    {
        var time = new ManualTimeProvider();
        var binding = Returning(call =>
        {
            time.Advance(TimeSpan.FromMinutes(1));
            return outcome switch
            {
                "found" => ScriptedReadBinding.Corpus(call),
                "gone" => ShardReadResult.Gone,
                "notfound" => ShardReadResult.NotFound,
                "failed" => throw new ShardFailureException(East, ShardFailureReason.Failed, null),
                _ => throw new ShardReadCircuitOpenException(East),
            };
        });
        using var host = ProxiedReadTests.Start(binding, setup => setup.Time = time);

        ReadResponse result = await ReadResponse.GetAsync(host, Dup, host.Token());

        result.Status.ShouldBe(504);
        result.IssueCode.ShouldBe("timeout");
        result.IsCleanFailure.ShouldBeTrue();
        result.Body.ShouldNotContain("Patient\"");
        result.Headers.ContainsKey("WWW-Authenticate").ShouldBeFalse();
    }

    [Fact]
    public async Task ADeadlineThatElapsedWithoutTimerDelivery_StillOutranksASuccessfulRead()
    {
        var time = new ManualTimeProvider { TimersEnabled = false };
        var binding = Returning(call =>
        {
            ShardReadResult found = ScriptedReadBinding.Corpus(call);
            time.Advance(TimeSpan.FromMinutes(1));
            call.Token.IsCancellationRequested.ShouldBeFalse();
            return found;
        });
        using var host = ProxiedReadTests.Start(binding, setup => setup.Time = time);

        ReadResponse result = await ReadResponse.GetAsync(host, Dup, host.Token());

        result.Status.ShouldBe(504);
        result.IsCleanFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task ARefusedSend_IsTooCostly()
    {
        var binding = Returning(call =>
        {
            // Two shards at one call each: the third attempt is over the request's allowance.
            call.Budget.BeforeBackendCall();
            call.Budget.BeforeBackendCall();
            call.Budget.BeforeBackendCall();
            return ShardReadResult.NotFound;
        });
        using var host = ProxiedReadTests.Start(binding, setup => setup.Settings["Federation:Limits:BackendCallsPerShard"] = "1");

        ReadResponse result = await ReadResponse.GetAsync(host, Dup, host.Token());

        result.Status.ShouldBe(400);
        result.IssueCode.ShouldBe("too-costly");
        result.IsCleanFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData("found")]
    [InlineData("gone")]
    [InlineData("failed")]
    public async Task ASwallowedBudgetBreach_StillOutranksTheBackendResult(string outcome)
    {
        var binding = Returning(call =>
        {
            // A binding that latches the breach but reports a result anyway must not turn it into a served resource.
            Should.Throw<RequestTooCostlyException>(() => call.Budget.OnResponseBytesConsumed(long.MaxValue));
            return outcome switch
            {
                "found" => ShardReadResult.Found(new ShardEntry(call.Key, null, null, SearchWorld.Slice("east", "Patient", "dup"))),
                "gone" => ShardReadResult.Gone,
                _ => throw new ShardFailureException(East, ShardFailureReason.Failed, null),
            };
        });
        using var host = ProxiedReadTests.Start(binding, setup => setup.Settings["Federation:Limits:MaxResponseBytes"] = "10");

        ReadResponse result = await ReadResponse.GetAsync(host, Dup, host.Token());

        result.Status.ShouldBe(400);
        result.IssueCode.ShouldBe("too-costly");
        result.IsCleanFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task ADefaultBindingThatChargesWithinTheBudget_Succeeds()
    {
        var binding = new ScriptedReadBinding();
        using var host = ProxiedReadTests.Start(binding, setup =>
        {
            setup.Settings["Federation:Limits:BackendCallsPerShard"] = "1";
            setup.Settings["Federation:Limits:MaxResponseBytes"] = SearchWorld.Slice("east", "Patient", "dup").Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        });

        ReadResponse result = await ReadResponse.GetAsync(host, Dup, host.Token());

        result.Status.ShouldBe(200);
        binding.Calls[0].Budget.BackendCalls.ShouldBe(1);
    }
}
