using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Capabilities.Refresh;
using Ignixa.Federation.Shards.Probes;
using Ignixa.Federation.Shards.UnitTests.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Probes;

/// <summary>
/// What <c>AddShardStartupProbes</c> accepts: the opt-in follows the shared backend, takes one resource type for every
/// registered shard, and refuses a selection that cannot be probed before any backend is contacted.
/// </summary>
public sealed class ShardStartupProbeRegistrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ANullServiceCollectionOrSelectionIsRefused()
    {
        Should.Throw<ArgumentNullException>(() => ShardStartupProbeExtensions.AddShardStartupProbes(null!, new Dictionary<ShardId, ResourceType>()));
        Should.Throw<ArgumentNullException>(() => new ServiceCollection().AddShardStartupProbes(null!));
    }

    [Fact]
    public async Task ANullResourceType_IsRefusedWhenRegistering()
    {
        await using var fixture = new StartupProbeHost(ShardHarness.Follow, ShardHarness.GetOnly);
        var selection = new Dictionary<ShardId, ResourceType> { [fixture.EastId] = StartupProbeHost.Patient, [fixture.WestId] = null! };
        var services = new ServiceCollection();

        Should.Throw<ArgumentException>(() => services.AddShardStartupProbes(selection));
    }

    [Fact]
    public void WithoutTheSharedBackend_RegistrationIsRefused()
    {
        var services = new ServiceCollection();

        Should.Throw<InvalidOperationException>(() => services.AddShardStartupProbes(new Dictionary<ShardId, ResourceType>()));
    }

    [Fact]
    public async Task RegisteringTwice_IsRefused_SoThereIsOneWorkerAndOneCheck()
    {
        await using var fixture = new StartupProbeHost(ShardHarness.Follow, ShardHarness.GetOnly);
        var services = new ServiceCollection();
        services.AddShardBackend(
            fixture.Registry,
            new SyntheticCredentials(),
            TimeSpan.FromSeconds(10),
            1 << 20,
            fixture.Registry.Shards.ToDictionary(s => s.Id, _ => SyntheticLimits.Generous));
        services.AddShardStartupProbes(fixture.PatientForEveryShard());

        Should.Throw<InvalidOperationException>(() => services.AddShardStartupProbes(fixture.PatientForEveryShard()));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("both")]
    public async Task ASelectionThatIsNotOneTypePerRegisteredShard_FailsHostStartupBeforeAnythingIsSent(string defect)
    {
        await using var fixture = new StartupProbeHost(ShardHarness.Follow, ShardHarness.GetOnly);
        Dictionary<ShardId, ResourceType> selection = fixture.PatientForEveryShard();
        if (defect is "missing" or "both")
        {
            selection.Remove(fixture.WestId);
        }

        if (defect is "unknown" or "both")
        {
            selection[ShardId.Parse("elsewhere")] = StartupProbeHost.Patient;
        }

        fixture.BuildHost(services => services.AddShardStartupProbes(selection));

        await Should.ThrowAsync<ArgumentException>(() => fixture.Host.StartAsync(Ct));

        fixture.East.Requests.ShouldBeEmpty();
        fixture.West.Requests.ShouldBeEmpty();
    }
}
