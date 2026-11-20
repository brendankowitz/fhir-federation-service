using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Shards;

public sealed class ShardCursorTests
{
    private static readonly ShardId East = ShardId.Parse("east");
    private static readonly ResourceType Patient = ResourceType.Parse("Patient");

    [Theory]
    [InlineData(NextLinkRedemption.FollowGet)]
    [InlineData(NextLinkRedemption.RepostToSearch)]
    public void KeepsOwnerTypeTargetAndRedemption(NextLinkRedemption redemption)
    {
        const string target = "https://east.example/fhir/Patient?name=Sarah&name=Sarah&code=a%2Cb&ct=xyz";

        var cursor = new ShardCursor(East, Patient, target, redemption);

        cursor.Shard.ShouldBe(East);
        cursor.Type.ShouldBe(Patient);
        cursor.Target.ShouldBe(target);
        cursor.Redemption.ShouldBe(redemption);
    }

    [Fact]
    public void ToString_OmitsTheBackendTarget()
    {
        var cursor = new ShardCursor(East, Patient, "https://east.example/fhir/Patient?name=Sarah&ct=xyz", NextLinkRedemption.FollowGet);

        string text = cursor.ToString();

        text.ShouldContain("east");
        text.ShouldContain("Patient");
        text.ShouldNotContain("Sarah");
        text.ShouldNotContain("east.example");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankTarget_IsRejected(string target)
    {
        Should.Throw<ArgumentException>(() => new ShardCursor(East, Patient, target, NextLinkRedemption.FollowGet));
    }

    [Fact]
    public void MissingPartsOrUndefinedRedemption_AreRejected()
    {
        Should.Throw<ArgumentNullException>(() => new ShardCursor(null!, Patient, "next", NextLinkRedemption.FollowGet));
        Should.Throw<ArgumentNullException>(() => new ShardCursor(East, null!, "next", NextLinkRedemption.FollowGet));
        Should.Throw<ArgumentException>(() => new ShardCursor(East, Patient, null!, NextLinkRedemption.FollowGet));
        Should.Throw<ArgumentOutOfRangeException>(() => new ShardCursor(East, Patient, "next", (NextLinkRedemption)9));
    }
}
