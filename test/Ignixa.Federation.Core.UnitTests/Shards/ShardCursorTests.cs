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

    // The emitted cursor carries the redemption for the stream's starting method, not the profile's raw policy.
    [Theory]
    [InlineData(ShardSearchMethod.PostSearch, NextLinkRedemption.RepostToSearch)]
    [InlineData(ShardSearchMethod.Get, NextLinkRedemption.FollowGet)]
    public void Next_TakesTheRedemptionForTheMethodTheStreamStartedWith(ShardSearchMethod startedWith, NextLinkRedemption expected)
    {
        var profile = new BackendProfile("P", SupportsPostSearch: true, 100, NextLinkRedemption.RepostToSearch, null);
        var request = new ShardSearchRequest(East, Patient, [new("name", "Sarah")], 20, startedWith);

        var cursor = ShardCursor.Next(request, profile, "https://east.example/fhir/Patient?name=Sarah&ct=xyz");

        cursor.ShouldBe(new ShardCursor(East, Patient, "https://east.example/fhir/Patient?name=Sarah&ct=xyz", expected));
    }

    [Fact]
    public void Next_RejectsMissingPartsAndABlankTarget()
    {
        var profile = new BackendProfile("P", SupportsPostSearch: true, 100, NextLinkRedemption.FollowGet, null);
        var request = new ShardSearchRequest(East, Patient, [], 20, ShardSearchMethod.Get);

        Should.Throw<ArgumentNullException>(() => ShardCursor.Next(null!, profile, "next")).ParamName.ShouldBe("request");
        Should.Throw<ArgumentNullException>(() => ShardCursor.Next(request, null!, "next")).ParamName.ShouldBe("profile");
        Should.Throw<ArgumentException>(() => ShardCursor.Next(request, profile, " "));
    }
}
