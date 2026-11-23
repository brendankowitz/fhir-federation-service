using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Ignixa.Federation.Api.Reads;
using Ignixa.Federation.Api.Search;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Tests.Integration.Reads;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Search;

/// <summary>
/// The proof-backed publication worker against the real configured host: it installs an admission only through the source's
/// commit gate, paces refusals, leaves a current admission alone, recovers from stale evidence through a fresh admission, and
/// once stopped never installs, even a valid result returned late.
/// </summary>
public sealed class CapabilityPublicationWorkerTests
{
    private const string Query = "/Patient?name=Orchid";
    private const string NotReady =
        """{"resourceType":"OperationOutcome","issue":[{"severity":"error","code":"transient","diagnostics":"Federation capabilities are not ready."}]}""";

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(30);
    private static readonly ResourceType Patient = ResourceType.Parse("Patient");

    [Fact]
    public async Task Refusal_IsPaced_Admission_Warms_CurrentIsNotReprobed_StaleIsColdAtOnce_LateCandidateIsRefusedAtTheGate_AndAFreshOneRecovers()
    {
        await using Harness h = Harness.Create();
        await h.Worker.StartAsync(TestContext.Current.CancellationToken);

        // An expected refusal leaves the host cold and the next attempt waits a full cadence.
        (await h.Source.NextAsync()).Release(null);
        StepTimer wait = await h.Clock.NextWaitAsync();
        wait.Due.ShouldBe(Cadence);
        h.Source.Calls.ShouldBe(1);
        await AssertColdAsync(h);

        // An admission warms the host through exactly one gated commit.
        wait.Fire();
        (await h.Source.NextAsync()).Release(h.Evidence.Admit(SearchWorld.Capabilities()));
        wait = await h.Clock.NextWaitAsync();
        h.Evidence.Commits.ShouldBe(1);
        await AssertWarmAsync(h);

        // A current admission is only rechecked, never reprobed.
        int checks = h.Evidence.Checks;
        wait.Fire();
        wait = await h.Clock.NextWaitAsync();
        wait.Due.ShouldBe(Cadence);
        h.Source.Calls.ShouldBe(2);
        h.Evidence.Checks.ShouldBeGreaterThan(checks);

        // Replaced evidence is cold at once, before the worker notices; the worker then probes without another cadence.
        h.Evidence.Replace();
        h.State.Current.ShouldBeNull();
        await AssertColdAsync(h);
        wait.Fire();
        Probe probe = await h.Source.NextAsync();
        h.Clock.PendingWaits.ShouldBe(0);

        // A candidate whose evidence is replaced while the worker builds it is refused by the source's gate; the replacement
        // completes during the build, so the build runs outside that gate.
        CapabilityAdmission late = h.Evidence.Admit(SearchWorld.Capabilities());
        bool replacedDuringBuild = false;
        h.Time.ArmOnce(() => replacedDuringBuild = Task.Run(h.Evidence.Replace).Wait(Guard));
        probe.Release(late);
        wait = await h.Clock.NextWaitAsync();
        replacedDuringBuild.ShouldBeTrue();
        h.Evidence.Refusals.ShouldBe(1);
        h.Evidence.Commits.ShouldBe(1);
        h.State.Current.ShouldBeNull();
        await AssertColdAsync(h);

        // Recovery is a fresh admission, one cadence later.
        wait.Fire();
        (await h.Source.NextAsync()).Release(h.Evidence.Admit(SearchWorld.Capabilities()));
        await h.Clock.NextWaitAsync();
        h.Evidence.Commits.ShouldBe(2);
        await AssertWarmAsync(h);
        h.Source.Calls.ShouldBe(4);
    }

    [Fact]
    public async Task StopDuringAPendingProbe_CancelsIt_WaitsForItsRelease_AndIgnoresAValidLateResult()
    {
        await using Harness h = Harness.Create();
        await h.Worker.StartAsync(TestContext.Current.CancellationToken);
        Probe probe = await h.Source.NextAsync();

        Task stop = h.Worker.StopAsync(TestContext.Current.CancellationToken);
        probe.Token.IsCancellationRequested.ShouldBeTrue();
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        stop.IsCompleted.ShouldBeFalse();

        probe.Release(h.Evidence.Admit(SearchWorld.Capabilities()));
        await stop.WaitAsync(Guard, TestContext.Current.CancellationToken);

        h.Worker.ExecuteTask.ShouldNotBeNull().IsCanceled.ShouldBeTrue();
        h.Evidence.Commits.ShouldBe(0);
        h.Evidence.Refusals.ShouldBe(0);
        h.Source.Calls.ShouldBe(1);
        await AssertColdAsync(h);
    }

    [Fact]
    public async Task StopWhileBuilding_ClosesThePath_SoACommitTheSourceStillAllowsInstallsNothing()
    {
        await using Harness h = Harness.Create();
        await h.Worker.StartAsync(TestContext.Current.CancellationToken);
        Probe probe = await h.Source.NextAsync();

        // A stop whose bound has already expired returns at once; the admission is still current at the gate.
        bool stopReturned = false;
        bool servedAfterCommit = true;
        h.Time.ArmOnce(() => stopReturned = h.Worker.StopAsync(new CancellationToken(canceled: true)).IsCompletedSuccessfully);
        h.Evidence.AfterInstall = () => servedAfterCommit = h.State.Current is not null;
        probe.Release(h.Evidence.Admit(SearchWorld.Capabilities()));
        Task execution = h.Worker.ExecuteTask.ShouldNotBeNull();
        await execution.WaitAsync(Guard, TestContext.Current.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);

        execution.IsCanceled.ShouldBeTrue();
        stopReturned.ShouldBeTrue();
        h.Evidence.Commits.ShouldBe(1);
        servedAfterCommit.ShouldBeFalse();
        h.State.Current.ShouldBeNull();
        await AssertColdAsync(h);
    }

    [Fact]
    public async Task StoppedWorker_StartsNoProbe()
    {
        await using Harness h = Harness.Create();
        await h.Worker.StopAsync(TestContext.Current.CancellationToken);

        await h.Worker.StartAsync(TestContext.Current.CancellationToken);
        await h.Worker.ExecuteTask.ShouldNotBeNull().WaitAsync(Guard, TestContext.Current.CancellationToken);

        h.Source.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task WorkerStartedAlreadyCancelled_StartsNoProbe()
    {
        await using Harness h = Harness.Create();

        await h.Worker.StartAsync(new CancellationToken(canceled: true));
        Task execution = h.Worker.ExecuteTask.ShouldNotBeNull();
        await execution.WaitAsync(Guard, TestContext.Current.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);

        execution.IsCanceled.ShouldBeTrue();
        h.Source.Calls.ShouldBe(0);
        await AssertColdAsync(h);
    }

    [Fact]
    public async Task RegisteredSeed_IsWithdrawnByTheFirstAttempt_SoTheHostIsColdUntilAnAdmissionIsInstalled()
    {
        await using Harness h = Harness.Create(setup =>
        {
            setup.Capabilities = SearchWorld.Capabilities();
            setup.WaitForReadiness = true;
        });
        h.State.Current.ShouldNotBeNull();

        await h.Worker.StartAsync(TestContext.Current.CancellationToken);
        Probe probe = await h.Source.NextAsync();
        h.State.Current.ShouldBeNull();
        await AssertColdAsync(h);

        probe.Release(null);
        await h.Clock.NextWaitAsync();
        await AssertColdAsync(h);
    }

    [Fact]
    public async Task BuildFailure_SurfacesFromTheWorker_WithoutACommit_AndTheHostStaysCold()
    {
        await using Harness h = Harness.Create();
        await h.Worker.StartAsync(TestContext.Current.CancellationToken);
        SearchCapabilities mismatched = SearchWorld.Capabilities(registry: SearchWorld.ReverseRegistry(extraWitnessShard: true));

        (await h.Source.NextAsync()).Release(h.Evidence.Admit(mismatched));

        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
            h.Worker.ExecuteTask.ShouldNotBeNull().WaitAsync(Guard, TestContext.Current.CancellationToken));
        failure.Message.ShouldContain("Federation:Shards");
        h.Evidence.Commits.ShouldBe(0);
        h.Evidence.Refusals.ShouldBe(0);
        await AssertColdAsync(h);
    }

    [Fact]
    public async Task ProvenSearchableButUnreadableType_KeepsItsSearch_ButHasNoReadCapabilityAlternateOrRoute()
    {
        var binding = new ScriptedReadBinding();
        await using Harness h = Harness.Create(setup => setup.ConfigureServices = services => services.AddSingleton<IShardReadBinding>(binding));
        await h.Worker.StartAsync(TestContext.Current.CancellationToken);
        (await h.Source.NextAsync()).Release(h.Evidence.Admit(SearchWorld.Capabilities(), Patient));
        await h.Clock.NextWaitAsync();
        await h.Host.WaitForReadinessAsync();

        JsonElement statement = JsonElement.Parse((await ReadResponse.GetAsync(h.Host, "/metadata", token: null)).Body);
        SearchResult observations = await h.Host.GetAsync("/Observation?code=oracle-code");
        SearchResult patients = await h.Host.GetAsync(Query);
        ReadResponse observation = await ReadResponse.GetAsync(h.Host, "/shards/k7q2/Observation/obs-dup", h.Host.Token());
        ReadResponse patient = await ReadResponse.GetAsync(h.Host, "/shards/k7q2/Patient/dup", h.Host.Token());

        ReadInteractions(statement).ShouldBe([("Observation", false), ("Patient", true)], ignoreOrder: true);
        observations.StatusCode.ShouldBe(HttpStatusCode.OK);
        observations.Entries.Select(e => e.FullUrl).ShouldBe([
            "https://fhir-east.example.org/Observation/obs-dup",
            "https://fhir-west.example.org/Observation/obs-dup",
            "https://fhir-west.example.org/Observation/obs-dup-xref",
        ]);
        Entries(observations).ShouldAllBe(e => !HasAlternate(e));
        Entries(patients).ShouldNotBeEmpty();
        Entries(patients).ShouldAllBe(e => HasAlternate(e));
        observation.Status.ShouldBe(404);
        observation.IssueCode.ShouldBe("not-found");
        patient.Status.ShouldBe(200);
        binding.Calls.ShouldHaveSingleItem().Key.Type.Value.ShouldBe("Patient");
    }

    [Fact]
    public void Cadence_ShorterThanAMillisecondOrTooLongForADelay_IsRejected()
    {
        using SearchHost host = SearchHost.Start(setup =>
        {
            setup.Capabilities = null;
            setup.WaitForReadiness = false;
        });
        var state = host.Services.GetRequiredService<PrimarySearchState>();
        var source = new ScriptedAdmissions();

        TimeSpan[] invalid =
        [
            TimeSpan.Zero,
            TimeSpan.FromTicks(1),
            TimeSpan.FromMilliseconds(1) - TimeSpan.FromTicks(1),
            TimeSpan.FromMilliseconds(-1),
            TimeSpan.FromMilliseconds(uint.MaxValue),
        ];
        foreach (TimeSpan cadence in invalid)
        {
            Should.Throw<ArgumentOutOfRangeException>(() => new CapabilityPublicationWorker(state, source, cadence, TimeProvider.System), cadence.ToString());
        }

        using var shortest = new CapabilityPublicationWorker(state, source, TimeSpan.FromMilliseconds(1), TimeProvider.System);
        using var longest = new CapabilityPublicationWorker(state, source, TimeSpan.FromMilliseconds(uint.MaxValue - 1), TimeProvider.System);
    }

    private static async Task AssertColdAsync(Harness h)
    {
        int shardCalls = h.Host.Shards.Calls.Count;
        SearchResult metadata = await h.Host.GetAsync("/metadata", token: null);
        metadata.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        metadata.Body.ShouldBe(NotReady);
        SearchResult search = await h.Host.GetAsync(Query);
        search.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        search.Body.ShouldBe(NotReady);
        h.Host.Shards.Calls.Count.ShouldBe(shardCalls);
    }

    private static async Task AssertWarmAsync(Harness h)
    {
        h.State.Current.ShouldNotBeNull();
        await h.Host.WaitForReadinessAsync();
        (await h.Host.GetAsync(Query)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static JsonElement[] Entries(SearchResult result) => [.. result.Json.GetProperty("entry").EnumerateArray()];

    private static bool HasAlternate(JsonElement entry) =>
        entry.TryGetProperty("link", out JsonElement links) && links.EnumerateArray().Any(l => l.GetProperty("relation").GetString() == "alternate");

    private static (string Type, bool Read)[] ReadInteractions(JsonElement statement) =>
        [.. statement.GetProperty("rest")[0].GetProperty("resource").EnumerateArray().Select(r => (
            r.GetProperty("type").GetString()!,
            r.GetProperty("interaction").EnumerateArray().Any(i => i.GetProperty("code").GetString() == "read")))];

    /// <summary>A cold configured host and an unstarted worker over it, with every collaborator under the test's control.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private Harness(SearchHost host, PrepareHookTime time)
        {
            Host = host;
            Time = time;
            State = host.Services.GetRequiredService<PrimarySearchState>();
            Worker = new CapabilityPublicationWorker(State, Source, Cadence, Clock);
        }

        public SearchHost Host { get; }

        public PrepareHookTime Time { get; }

        public PrimarySearchState State { get; }

        public StepClock Clock { get; } = new();

        public ScriptedAdmissions Source { get; } = new();

        public ProofEvidence Evidence { get; } = new();

        public CapabilityPublicationWorker Worker { get; }

        public static Harness Create(Action<SearchHostSetup>? configure = null)
        {
            var time = new PrepareHookTime();
            SearchHost host = SearchHost.Start(setup =>
            {
                setup.Capabilities = null;
                setup.WaitForReadiness = false;
                setup.Time = time;
                configure?.Invoke(setup);
            });
            return new Harness(host, time);
        }

        public async ValueTask DisposeAsync()
        {
            Source.Close();
            using var bound = new CancellationTokenSource(Guard);
            await Worker.StopAsync(bound.Token);
            Worker.Dispose();
            Host.Dispose();
        }
    }

    /// <summary>The host's clock, which runs one armed action the next time the current time is read, as building a generation does.</summary>
    private sealed class PrepareHookTime : TimeProvider
    {
        private Action? _next;

        public void ArmOnce(Action action) => Volatile.Write(ref _next, action);

        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Exchange(ref _next, null)?.Invoke();
            return base.GetUtcNow();
        }
    }

    /// <summary>The worker's clock: each wait is surfaced to the test, and elapses only when the test fires it.</summary>
    private sealed class StepClock : TimeProvider
    {
        private readonly Channel<StepTimer> _waits = Channel.CreateUnbounded<StepTimer>();

        public int PendingWaits => _waits.Reader.Count;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new StepTimer(callback, state, dueTime);
            _waits.Writer.TryWrite(timer);
            return timer;
        }

        public Task<StepTimer> NextWaitAsync() => _waits.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Guard);
    }

    private sealed class StepTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        public TimeSpan Due { get; } = due;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>One pending probe. It ignores its token: only the test completes it, so a stop can be held behind it.</summary>
    private sealed class Probe(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;

        public TaskCompletionSource<CapabilityAdmission?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release(CapabilityAdmission? admission) => Result.TrySetResult(admission);
    }

    private sealed class ScriptedAdmissions : ICapabilityAdmissionSource
    {
        private readonly Lock _gate = new();
        private readonly Channel<Probe> _probes = Channel.CreateUnbounded<Probe>();
        private readonly List<Probe> _issued = [];
        private bool _closed;

        public int Calls
        {
            get
            {
                lock (_gate)
                {
                    return _issued.Count;
                }
            }
        }

        public ValueTask<CapabilityAdmission?> TryAdmitAsync(CancellationToken cancellationToken)
        {
            var probe = new Probe(cancellationToken);
            lock (_gate)
            {
                _issued.Add(probe);
                if (_closed)
                {
                    return ValueTask.FromResult<CapabilityAdmission?>(null);
                }

                _probes.Writer.TryWrite(probe);
            }

            return new ValueTask<CapabilityAdmission?>(probe.Result.Task);
        }

        public Task<Probe> NextAsync() => _probes.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Guard);

        /// <summary>Refuses every probe issued so far that is still pending, and later ones at once, so the worker can be joined.</summary>
        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
                foreach (Probe probe in _issued)
                {
                    probe.Release(null);
                }
            }
        }
    }

    /// <summary>Replaceable evidence with a commit gate: an admission is current until the evidence is replaced.</summary>
    private sealed class ProofEvidence
    {
        private readonly Lock _gate = new();
        private int _generation;
        private int _checks;
        private int _commits;
        private int _refusals;

        public int Checks => Volatile.Read(ref _checks);

        public int Commits => Volatile.Read(ref _commits);

        public int Refusals => Volatile.Read(ref _refusals);

        /// <summary>Gets or sets an observation run inside the gate right after a committed install, before anything else can run.</summary>
        public Action? AfterInstall { get; set; }

        public CapabilityAdmission Admit(SearchCapabilities view, params ResourceType[] readable)
        {
            int generation = Volatile.Read(ref _generation);
            bool IsCurrent()
            {
                Interlocked.Increment(ref _checks);
                return Volatile.Read(ref _generation) == generation;
            }

            bool TryCommitIfCurrent(Action install)
            {
                lock (_gate)
                {
                    if (Volatile.Read(ref _generation) != generation)
                    {
                        Interlocked.Increment(ref _refusals);
                        return false;
                    }

                    install();
                    Interlocked.Increment(ref _commits);
                    AfterInstall?.Invoke();
                    return true;
                }
            }

            return new CapabilityAdmission(view, readable, IsCurrent, TryCommitIfCurrent);
        }

        public void Replace()
        {
            lock (_gate)
            {
                Interlocked.Increment(ref _generation);
            }
        }
    }
}
