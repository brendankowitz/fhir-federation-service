using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Capabilities;
using Ignixa.Federation.Shards.Capabilities.Refresh;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;

/// <summary>
/// A refresher over three synthetic shards (<c>east</c>, <c>west</c>, <c>north</c>, in that registry order) with a manual
/// clock and a scripted fetch seam. Nothing touches the network or the real clock.
/// </summary>
internal sealed class RefreshRig : IAsyncDisposable
{
    private static readonly BackendProfile Profile = new("Synthetic", SupportsPostSearch: false, MaxCount: 50, NextLinkRedemption.FollowGet, null);

    private static readonly string[] ShardNames = ["east", "west", "north"];

    private readonly CancellationTokenSource _stop = new();
    private Task? _run;

    public RefreshRig(bool honorsCancellation = true)
    {
        Script = new FetchScript(honorsCancellation);
        Registry = ShardRegistry.Create(ShardNames.Select(Options), [Profile]);
        Refresher = new ShardCapabilityRefresher(Registry, Script.Fetch, Time);
    }

    public ManualTimeProvider Time { get; } = new();

    public FetchScript Script { get; }

    public ShardRegistry Registry { get; }

    public ShardCapabilityRefresher Refresher { get; }

    public ShardDescriptor East => Registry.Shards[0];

    public ShardDescriptor West => Registry.Shards[1];

    public ShardDescriptor North => Registry.Shards[2];

    public Task Run => _run ?? throw new InvalidOperationException("Start the rig first.");

    public void Start() => _run = Refresher.RunAsync(_stop.Token);

    public void Stop() => _stop.Cancel();

    /// <summary>Waits, under the watchdog, for the loop to end; it rethrows the loop's failure, if it failed.</summary>
    public Task EndedAsync() => Run.WaitAsync(Watchdog.Interval);

    /// <summary>
    /// Answers one whole sweep, one answer per shard in registry order: evidence succeeds, an exception fails. Returns once
    /// the last answer has been published.
    /// </summary>
    public Task SweepAsync(params object[] answers) => SweepAsync(null, answers);

    /// <summary>As <see cref="SweepAsync(object[])"/>, for a sweep whose first fetch the test already took from the script.</summary>
    public async Task SweepAsync(PendingFetch? first, object[] answers)
    {
        for (int i = 0; i < answers.Length; i++)
        {
            PendingFetch call = i == 0 && first is not null ? first : await Script.NextAsync();
            call.Shard.ShouldBeSameAs(Registry.Shards[i]);
            ShardCapabilityEntry before = Refresher.Current.Shards[call.Shard.Id];
            switch (answers[i])
            {
                case ShardCapabilityEvidence evidence:
                    call.Succeed(evidence);
                    break;
                case Exception failure:
                    call.Fail(failure);
                    break;
                default:
                    throw new ArgumentException("Each answer is evidence or an exception.", nameof(answers));
            }

            await Watchdog.EventuallyAsync(
                () => !ReferenceEquals(Refresher.Current.Shards[call.Shard.Id], before),
                $"the answer for {call.Shard.Alias.Value} to be published");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        try
        {
            if (_run is not null)
            {
                // WhenAny observes completion without rethrowing: a test that expects a fault has already asserted it.
                await Task.WhenAny(_run).WaitAsync(Watchdog.Interval);
            }
        }
        finally
        {
            _stop.Dispose();
        }
    }

    private static ShardOptions Options(string name) => new()
    {
        Id = name,
        Alias = name,
        BaseUrl = $"https://{name}.example.invalid/fhir",
        Profile = Profile.Name,
        Auth = new AuthOptions { Type = "ManagedIdentity", Resource = "api://synthetic" },
    };
}
