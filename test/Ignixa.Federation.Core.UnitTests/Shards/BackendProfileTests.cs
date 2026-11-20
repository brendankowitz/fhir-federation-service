using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Shards;

public sealed class BackendProfileTests
{
    [Fact]
    public void KeepsCertifiedFacts()
    {
        var order = new LastUpdatedOrder(TimeSpan.FromMilliseconds(1), StableTies: true);

        var profile = new BackendProfile("MicrosoftFhirServer", SupportsPostSearch: true, MaxCount: 1000, NextLinkRedemption.RepostToSearch, order);

        profile.Name.ShouldBe("MicrosoftFhirServer");
        profile.SupportsPostSearch.ShouldBeTrue();
        profile.MaxCount.ShouldBe(1000);
        profile.NextLinkRedemption.ShouldBe(NextLinkRedemption.RepostToSearch);
        profile.LastUpdatedOrder.ShouldBe(order);
        new BackendProfile("Other", false, 1, NextLinkRedemption.FollowGet, null).LastUpdatedOrder.ShouldBeNull();
        new LastUpdatedOrder(TimeSpan.FromSeconds(1), StableTies: false).StableTies.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void BlankName_IsRejected(string? name)
    {
        Should.Throw<ArgumentException>(() => new BackendProfile(name!, true, 100, NextLinkRedemption.FollowGet, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveMaxCount_IsRejected(int maxCount)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new BackendProfile("P", true, maxCount, NextLinkRedemption.FollowGet, null));
    }

    [Fact]
    public void UndefinedRedemption_IsRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new BackendProfile("P", true, 100, (NextLinkRedemption)5, null));
    }

    // RepostToSearch re-posts to [type]/_search, so a backend without POST search cannot certify it.
    [Fact]
    public void RepostToSearch_WithoutPostSearch_IsRejected()
    {
        Should.Throw<ArgumentException>(() => new BackendProfile("P", SupportsPostSearch: false, 100, NextLinkRedemption.RepostToSearch, null))
            .ParamName.ShouldBe("NextLinkRedemption");
    }

    [Fact]
    public void FollowGet_WithoutPostSearch_IsLegal()
    {
        new BackendProfile("P", SupportsPostSearch: false, 100, NextLinkRedemption.FollowGet, null).SupportsPostSearch.ShouldBeFalse();
    }

    // The policy applies to streams started with POST; a stream started with GET always follows its next link with GET.
    [Theory]
    [InlineData(NextLinkRedemption.RepostToSearch, ShardSearchMethod.PostSearch, NextLinkRedemption.RepostToSearch)]
    [InlineData(NextLinkRedemption.RepostToSearch, ShardSearchMethod.Get, NextLinkRedemption.FollowGet)]
    [InlineData(NextLinkRedemption.FollowGet, ShardSearchMethod.PostSearch, NextLinkRedemption.FollowGet)]
    [InlineData(NextLinkRedemption.FollowGet, ShardSearchMethod.Get, NextLinkRedemption.FollowGet)]
    public void RedemptionFor_AppliesThePolicyOnlyToPostStartedStreams(
        NextLinkRedemption policy,
        ShardSearchMethod startedWith,
        NextLinkRedemption expected)
    {
        new BackendProfile("P", SupportsPostSearch: true, 100, policy, null).RedemptionFor(startedWith).ShouldBe(expected);
    }

    [Fact]
    public void RedemptionFor_AnUndefinedMethod_IsRejected()
    {
        var profile = new BackendProfile("P", SupportsPostSearch: true, 100, NextLinkRedemption.FollowGet, null);

        Should.Throw<ArgumentOutOfRangeException>(() => profile.RedemptionFor((ShardSearchMethod)5)).ParamName.ShouldBe("startedWith");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveOrderPrecision_IsRejected(long ticks)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new LastUpdatedOrder(TimeSpan.FromTicks(ticks), StableTies: true));
    }
}
