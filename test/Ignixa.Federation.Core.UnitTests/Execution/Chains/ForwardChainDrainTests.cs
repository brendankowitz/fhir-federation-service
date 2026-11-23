using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Chains;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Chains.ChainScenario;

namespace Ignixa.Federation.Core.UnitTests.Execution.Chains;

/// <summary>
/// Draining one distributed chain's witness searches across every shard, within the key cap, the payload and fetch
/// budgets, the subquery and per-call deadlines and the parallelism limit (design spec §7.5, §8).
/// </summary>
public sealed class ForwardChainDrainTests
{
    private const string Chain = "subject:Patient.name=Orchid";
    private const string TwoChains = "subject:Patient.name=Orchid&performer:Practitioner.name=Doc";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NoWitnessOnAnyShard_IsProvenEmpty_AndLaterChainsAreNotSearched()
    {
        ShardRegistry registry = Registry();
        var client = new FakeShardClient();
        ServePages(client, East, Patient, []);
        ServePages(client, West, Patient, []);

        ForwardChainResolution result = await Resolver(client, registry).ResolveAsync(Plan(registry, TwoChains), Token, fetchBudget: 10);

        result.ShouldBeOfType<ForwardChainEmpty>().Ordinal.ShouldBe(0);
        result.RemainingFetchBudget.ShouldBe(8);
        result.RemainingPayloadBytes.ShouldBe(1_000_000);
        client.Calls.Select(c => c.Search!.Type).ShouldBe([Patient, Patient]);
    }

    [Fact]
    public async Task WitnessSearchAsksEachShardForItsMaximumPage_ByItsProfilesRewriteMethod()
    {
        ShardRegistry registry = GetOnlyEastRegistry();
        var client = new FakeShardClient();
        ServePages(client, East, Patient, ["p1"]);
        ServePages(client, West, Patient, []);

        await Resolver(client, registry).ResolveAsync(Plan(registry, Chain), Token);

        client.Calls.Select(c => (c.Shard, c.Search!.PageSize, c.Search.Method)).ShouldBe(
            [(East, 500, ShardSearchMethod.Get), (West, 1000, ShardSearchMethod.PostSearch)],
            ignoreOrder: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctKeysUpToTheCap_AreReady_AndOneMoreIsTooMany(bool oneMore)
    {
        ShardRegistry registry = Registry();
        var client = new FakeShardClient();

        // A duplicate across pages counts once; a short and an empty page that carry next are followed.
        ServePages(client, East, Patient, ["p1"], [], ["p1", "p2"]);
        ServePages(client, West, Patient, oneMore ? ["w1"] : []);
        ForwardChainResolver resolver = Resolver(client, registry, new LimitOptions { MaxChainKeys = 2 });

        ForwardChainResolution result = await resolver.ResolveAsync(Plan(registry, Chain), Token, fetchBudget: 10);

        result.RemainingFetchBudget.ShouldBe(6);
        if (oneMore)
        {
            ForwardChainRefusal refusal = result.ShouldBeOfType<ForwardChainRefusal>();
            (refusal.Reason, refusal.Ordinal, refusal.Shard).ShouldBe((ForwardChainRefusalReason.TooManyKeys, 0, (ShardId?)West));
        }
        else
        {
            result.ShouldBeOfType<ForwardChainReady>().Witnesses[0].ShouldBe([Key(East, Patient, "p1"), Key(East, Patient, "p2")]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task WitnessPayloadIsChargedAgainstTheRequestBudget(int slack)
    {
        ShardRegistry registry = Registry();
        var client = new FakeShardClient();
        ServePages(client, East, Patient, ["p1"]);
        ServePages(client, West, Patient, ["w1"]);
        long entryBytes = Fixture.Entry(Key(East, Patient, "p1")).Resource.Length;

        ForwardChainResolution result = await Resolver(client, registry)
            .ResolveAsync(Plan(registry, Chain), Token, payloadBytes: (2 * entryBytes) + slack);

        if (slack == 0)
        {
            result.ShouldBeOfType<ForwardChainReady>().RemainingPayloadBytes.ShouldBe(0);
        }
        else
        {
            ForwardChainRefusal refusal = result.ShouldBeOfType<ForwardChainRefusal>();
            (refusal.Reason, refusal.Ordinal, refusal.Shard).ShouldBe((ForwardChainRefusalReason.PayloadBudgetExceeded, 0, (ShardId?)West));
        }
    }

    [Fact]
    public async Task WitnessCallsSpendTheFetchBudget_AndExhaustingItFails()
    {
        ShardRegistry registry = Registry();
        var client = new FakeShardClient();
        ServePages(client, East, Patient, ["p1"], ["p2"]);
        ServePages(client, West, Patient, ["w1"]);

        await Should.ThrowAsync<FetchBudgetExhaustedException>(Resolver(client, registry).ResolveAsync(Plan(registry, Chain), Token, fetchBudget: 2));
        client.InFlight.ShouldBe(0);
    }

    [Fact]
    public async Task DuplicateKeyInAWitnessPage_IsAContractViolation()
    {
        var client = new FakeShardClient();
        client.OnSearch(East, (_, _) => Task.FromResult(Witness(East, Patient, null, "p1", "p1")));

        await ShouldViolateContractAsync(client, East);
    }

    [Fact]
    public async Task WitnessOfAnotherType_IsAContractViolation()
    {
        var client = new FakeShardClient();
        client.OnSearch(East, (_, _) => Task.FromResult(Witness(East, Practitioner, null, "p1")));

        await ShouldViolateContractAsync(client, East);
    }

    [Fact]
    public async Task WitnessFromAnotherShard_IsAContractViolation()
    {
        var client = new FakeShardClient();
        client.OnSearch(East, (_, _) => Task.FromResult(Witness(West, Patient, null, "p1")));

        await ShouldViolateContractAsync(client, East);
    }

    [Fact]
    public async Task CursorWithAnotherRedemption_IsAContractViolation()
    {
        var client = new FakeShardClient();
        var foreign = new ShardCursor(East, Patient, "https://east.example/fhir/Patient?ct=1", NextLinkRedemption.FollowGet);
        client.OnSearch(East, (_, _) => Task.FromResult(Witness(East, Patient, foreign, "p1")));

        await ShouldViolateContractAsync(client, East);
    }

    [Fact]
    public async Task CursorThatRepeats_IsAContractViolation()
    {
        var client = new FakeShardClient();
        client.OnSearch(East, (request, _) =>
        {
            ShardCursor next = ShardCursor.Next(request, TestShards.MicrosoftFhirServer, "https://east.example/fhir/Patient?ct=1");
            client.OnContinue(next, Witness(East, Patient, next, "p2"));
            return Task.FromResult(Witness(East, Patient, next, "p1"));
        });

        await ShouldViolateContractAsync(client, East);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task CursorReturningToAnEarlierLink_FailsOnThePageThatNamesItAgain(int links)
    {
        var client = new FakeShardClient();
        client.OnSearch(East, (request, _) =>
        {
            ShardCursor[] cursors =
            [
                .. Enumerable.Range(1, links).Select(i => ShardCursor.Next(
                    request,
                    TestShards.MicrosoftFhirServer,
                    $"https://east.example/fhir/Patient?ct={i}")),
            ];
            for (int i = 0; i < links; i++)
            {
                client.OnContinue(cursors[i], Witness(East, Patient, cursors[(i + 1) % links], $"p{i + 2}"));
            }

            return Task.FromResult(Witness(East, Patient, cursors[0], "p1"));
        });

        await ShouldViolateContractAsync(client, East);

        // The first page and one continuation per link; the page naming ct=1 again is rejected before ct=1 is redeemed twice.
        client.Calls.Count(c => c.Shard == East).ShouldBe(links + 1);
    }

    [Theory]
    [InlineData(LateOutcome.Cancelled)]
    [InlineData(LateOutcome.Page)]
    [InlineData(LateOutcome.Fault)]
    public async Task CallerCancellation_WaitsForTheCallToEnd_AndWinsOverWhatItReturns(LateOutcome outcome)
    {
        ShardRegistry registry = Registry();
        var client = new FakeShardClient();
        var late = new LateCall();
        late.Serve(client, East, Patient, outcome);
        ServePages(client, West, Patient, []);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);

        Task<ForwardChainResolution> resolving = Resolver(client, registry).ResolveAsync(Plan(registry, Chain), caller.Token);
        await client.WhenStarted(1);
        await caller.CancelAsync();
        await late.ShouldHoldAsync(resolving, client);

        OperationCanceledException thrown = await Should.ThrowAsync<OperationCanceledException>(resolving);
        thrown.CancellationToken.ShouldBe(caller.Token);
        client.InFlight.ShouldBe(0);
    }

    [Fact]
    public async Task CallerCancellationWhileJoiningSpeculation_WinsOverTheWitnessFailure()
    {
        ShardRegistry registry = Registry();
        var client = new FakeShardClient();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var siblingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joinStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSibling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateTimeout = TimeSpan.FromSeconds(10);

        client.OnSearch(East, async (_, _) =>
        {
            await siblingStarted.Task.WaitAsync(gateTimeout, Token);
            return Witness(East, Practitioner, null, "wrong-type");
        });
        client.OnSearch(West, async (_, ct) =>
        {
            using CancellationTokenRegistration registration = ct.Register(() => joinStarted.TrySetResult());
            siblingStarted.SetResult();
            await releaseSibling.Task;
            return Witness(West, Patient, null);
        });

        var limits = new LimitOptions { MaxParallelBackendCalls = 2 };
        var timeouts = new TimeoutOptions { ChainSubquery = TimeSpan.FromMinutes(1), BackendCall = TimeSpan.FromMinutes(1) };
        Task<ForwardChainResolution> resolving = Resolver(client, registry, limits, timeouts)
            .ResolveAsync(Plan(registry, Chain), caller.Token);
        try
        {
            // Cleanup cancels the speculative token before waiting for the still-running sibling.
            await joinStarted.Task.WaitAsync(gateTimeout, Token);
            await caller.CancelAsync().WaitAsync(gateTimeout, Token);
            resolving.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            releaseSibling.TrySetResult();
        }

        OperationCanceledException thrown = await Should.ThrowAsync<OperationCanceledException>(
            () => resolving.WaitAsync(gateTimeout, Token));
        thrown.CancellationToken.ShouldBe(caller.Token);
        client.InFlight.ShouldBe(0);
    }

    [Theory]
    [InlineData(LateOutcome.Cancelled)]
    [InlineData(LateOutcome.Page)]
    [InlineData(LateOutcome.Fault)]
    public async Task ChainSubqueryDeadline_WaitsForTheCallsToEnd_AndFailsWithTheChainOrdinal(LateOutcome outcome)
    {
        ShardRegistry registry = Registry();
        var client = new FakeShardClient();
        var late = new LateCall();
        late.Serve(client, East, Practitioner, outcome, otherwise: "p1");
        late.Serve(client, West, Practitioner, outcome);
        var timeouts = new TimeoutOptions { ChainSubquery = TimeSpan.FromMilliseconds(100), BackendCall = TimeSpan.FromMinutes(1) };

        Task<ForwardChainResolution> resolving = Resolver(client, registry, timeouts: timeouts).ResolveAsync(Plan(registry, TwoChains), Token);
        await late.ShouldHoldAsync(resolving, client);

        ChainSubqueryTimeoutException thrown = await Should.ThrowAsync<ChainSubqueryTimeoutException>(resolving);
        thrown.Ordinal.ShouldBe(1);
        thrown.ShouldBeAssignableTo<TimeoutException>();
        client.InFlight.ShouldBe(0);
    }

    [Theory]
    [InlineData(LateOutcome.Cancelled)]
    [InlineData(LateOutcome.Page)]
    [InlineData(LateOutcome.Fault)]
    public async Task BackendCallDeadline_WaitsForTheCallToEnd_AndIsAShardTimeout(LateOutcome outcome)
    {
        ShardRegistry registry = Registry();
        var client = new FakeShardClient();
        var late = new LateCall();
        late.Serve(client, East, Patient, outcome);
        ServePages(client, West, Patient, []);
        var timeouts = new TimeoutOptions { BackendCall = TimeSpan.FromMilliseconds(100), ChainSubquery = TimeSpan.FromMinutes(1) };

        Task<ForwardChainResolution> resolving = Resolver(client, registry, timeouts: timeouts).ResolveAsync(Plan(registry, Chain), Token);
        await late.ShouldHoldAsync(resolving, client);

        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(resolving);
        (thrown.Shard, thrown.Reason).ShouldBe((East, ShardFailureReason.Timeout));
        client.InFlight.ShouldBe(0);
    }

    [Theory]
    [InlineData(Chain, "Observation.subject:Patient")] // A passthrough chain: no witness search.
    [InlineData("code=a", null)] // No chain at all.
    [InlineData("code=a&_sort=-_lastUpdated", null)] // Refused before any request.
    public async Task CancelledCaller_GetsItsCancellation_EvenWhenNoWitnessIsSearched(string query, string? passthrough)
    {
        ShardRegistry registry = Registry();
        var client = new FakeShardClient();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await caller.CancelAsync();
        QueryPlan plan = passthrough is null ? Plan(registry, query) : Plan(registry, query, passthrough);

        OperationCanceledException thrown = await Should.ThrowAsync<OperationCanceledException>(
            Resolver(client, registry).ResolveAsync(plan, caller.Token));

        thrown.CancellationToken.ShouldBe(caller.Token);
        client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task CancellationDuringTheLastWitnessCall_IsNotAnsweredWithAnOutcome()
    {
        ShardRegistry registry = Registry();
        var client = new FakeShardClient();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        ServePages(client, East, Patient, []);
        client.OnSearch(West, async (_, _) =>
        {
            await caller.CancelAsync(); // The page still arrives, but after the caller gave up.
            return Witness(West, Patient, null);
        });

        OperationCanceledException thrown = await Should.ThrowAsync<OperationCanceledException>(
            Resolver(client, registry).ResolveAsync(Plan(registry, Chain), caller.Token));

        thrown.CancellationToken.ShouldBe(caller.Token);
        client.InFlight.ShouldBe(0);
    }

    [Fact]
    public async Task WitnessCallsNeverExceedTheParallelLimit()
    {
        ShardRegistry registry = Fixture.Registry(4);
        var client = new FakeShardClient();
        var gate = new TaskCompletionSource();
        foreach (ShardDescriptor shard in registry.Shards)
        {
            client.OnSearch(shard.Id, async (_, ct) =>
            {
                await gate.Task.WaitAsync(ct);
                return Witness(shard.Id, Patient, null);
            });
        }

        Task<ForwardChainResolution> resolving = Resolver(client, registry, new LimitOptions { MaxParallelBackendCalls = 2 })
            .ResolveAsync(Plan(registry, Chain), Token);
        await client.WhenStarted(2);
        await Task.Delay(50, Token);
        client.Calls.Count.ShouldBe(2);
        gate.SetResult();

        (await resolving).ShouldBeOfType<ForwardChainEmpty>();
        client.Calls.Count.ShouldBe(4);
        client.MaxInFlight.ShouldBe(2);
    }

    /// <summary>Serves <paramref name="pages"/> of <paramref name="type"/> on <paramref name="shard"/>, linked by owned cursors.</summary>
    private static void ServePages(FakeShardClient client, ShardId shard, ResourceType type, params string[][] pages) =>
        client.OnSearch(shard, (request, _) =>
        {
            if (request.Type != type)
            {
                throw new InvalidOperationException("The test did not script this type.");
            }

            ShardPage[] linked = new ShardPage[Math.Max(1, pages.Length)];
            for (int i = linked.Length - 1; i >= 0; i--)
            {
                ShardCursor? next = i + 1 < linked.Length
                    ? ShardCursor.Next(request, TestShards.MicrosoftFhirServer, $"https://{shard}.example/fhir/{type.Value}?ct={i + 1}")
                    : null;
                linked[i] = Witness(shard, type, next, pages.Length == 0 ? [] : pages[i]);
                if (i > 0)
                {
                    ShardCursor cursor = ShardCursor.Next(request, TestShards.MicrosoftFhirServer, $"https://{shard}.example/fhir/{type.Value}?ct={i}");
                    client.OnContinue(cursor, linked[i]);
                }
            }

            return Task.FromResult(linked[0]);
        });

    /// <summary>How a <see cref="LateCall"/> ends once the test releases it.</summary>
    public enum LateOutcome
    {
        /// <summary>It honours the cancellation, after its cleanup.</summary>
        Cancelled,

        /// <summary>It returns a page anyway.</summary>
        Page,

        /// <summary>It fails with an error of its own.</summary>
        Fault,
    }

    /// <summary>
    /// Shard calls that end only when the test releases them, like a client still cleaning up after cancellation, so a test
    /// can prove the resolver waits for the actual call rather than abandoning it.
    /// </summary>
    private sealed class LateCall
    {
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Holds <paramref name="type"/> searches on <paramref name="shard"/>; other types return <paramref name="otherwise"/>.</summary>
        public void Serve(FakeShardClient client, ShardId shard, ResourceType type, LateOutcome outcome, params string[] otherwise) =>
            client.OnSearch(shard, async (request, ct) =>
            {
                if (request.Type != type)
                {
                    return Witness(shard, request.Type, null, otherwise);
                }

                using (ct.Register(() => _cancelled.TrySetResult()))
                {
                    await _release.Task;
                }

                return outcome switch
                {
                    LateOutcome.Cancelled => throw new OperationCanceledException(ct),
                    LateOutcome.Page => Witness(shard, type, null, "late"),
                    _ => throw new InvalidOperationException("A failure after cancellation."),
                };
            });

        /// <summary>
        /// Waits until a held call's token is cancelled, asserts that <paramref name="resolving"/> still waits for the call,
        /// then releases every held call.
        /// </summary>
        public async Task ShouldHoldAsync(Task resolving, FakeShardClient client)
        {
            try
            {
                await _cancelled.Task.WaitAsync(Token);
                (await Task.WhenAny(resolving, Task.Delay(100, Token))).ShouldNotBe(resolving);
                client.InFlight.ShouldBeGreaterThan(0);
            }
            finally
            {
                _release.TrySetResult();
            }
        }
    }

    private static async Task ShouldViolateContractAsync(FakeShardClient client, ShardId shard)
    {
        ShardRegistry registry = Registry();
        ServePagesIfUnscripted(client, registry);

        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(
            Resolver(client, registry).ResolveAsync(Plan(registry, Chain), Token));

        (thrown.Shard, thrown.Reason).ShouldBe((shard, ShardFailureReason.ContractViolation));
        client.InFlight.ShouldBe(0);
    }

    private static void ServePagesIfUnscripted(FakeShardClient client, ShardRegistry registry)
    {
        foreach (ShardDescriptor shard in registry.Shards.Where(s => s.Id != East))
        {
            ServePages(client, shard.Id, Patient, []);
        }
    }
}
