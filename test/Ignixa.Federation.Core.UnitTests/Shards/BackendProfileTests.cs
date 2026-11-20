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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveOrderPrecision_IsRejected(long ticks)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new LastUpdatedOrder(TimeSpan.FromTicks(ticks), StableTies: true));
    }
}
