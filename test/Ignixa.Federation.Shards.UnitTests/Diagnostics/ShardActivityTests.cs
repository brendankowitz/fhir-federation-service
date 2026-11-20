using System.Diagnostics;
using Ignixa.Federation.Shards.Diagnostics;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Diagnostics;

public sealed class ShardActivityTests
{
    [Fact]
    public void StartBackendCall_ReturnsClientActivityWithAliasAndStageTags()
    {
        using ActivityListener listener = ListenToFederation();

        using Activity? activity = ShardActivity.StartBackendCall("alpha", "search");

        activity.ShouldNotBeNull();
        activity.Kind.ShouldBe(ActivityKind.Client);
        activity.OperationName.ShouldBe("shard search");
        activity.GetTagItem(ShardActivity.ShardAliasTag).ShouldBe("alpha");
        activity.GetTagItem(ShardActivity.StageTag).ShouldBe("search");
    }

    [Fact]
    public void TagNames_AreTheDocumentedValues()
    {
        ShardActivity.ShardAliasTag.ShouldBe("federation.shard.alias");
        ShardActivity.StageTag.ShouldBe("federation.stage");
    }

    [Theory]
    [InlineData("", "search")]
    [InlineData("   ", "search")]
    [InlineData("alpha", "")]
    [InlineData("alpha", "   ")]
    public void StartBackendCall_WithBlankArgument_Throws(string alias, string stage)
    {
        Should.Throw<ArgumentException>(() => ShardActivity.StartBackendCall(alias, stage));
    }

    [Fact]
    public void ShardsAssembly_HasNoAspNetCoreReferences()
    {
        string[] aspNet = typeof(ShardActivity).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .ToArray();

        aspNet.ShouldBeEmpty("Shards must stay free of ASP.NET Core.");
    }

    private static ActivityListener ListenToFederation()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Ignixa.Federation",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
