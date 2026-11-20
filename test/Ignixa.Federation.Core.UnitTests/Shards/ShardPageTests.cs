using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Shards;

public sealed class ShardPageTests
{
    private static readonly ShardId East = ShardId.Parse("east");
    private static readonly ResourceType Patient = ResourceType.Parse("Patient");
    private static readonly ShardCursor Next = new(East, Patient, "https://east.example/fhir/Patient?ct=abc", NextLinkRedemption.FollowGet);

    [Fact]
    public void EmptyPageWithNext_IsNotExhausted()
    {
        var page = new ShardPage([], Next, null);

        page.Entries.ShouldBeEmpty();
        page.Next.ShouldBe(Next);
        page.IsExhausted.ShouldBeFalse();
    }

    [Fact]
    public void PageWithoutNext_IsExhausted()
    {
        new ShardPage([Entry("A1")], null, null).IsExhausted.ShouldBeTrue();
    }

    [Fact]
    public void MissingTotal_IsNotZero()
    {
        new ShardPage([], null, null).Total.ShouldBeNull();
        new ShardPage([], null, 0).Total.ShouldBe(0);
        new ShardPage([], null, 42).Total.ShouldBe(42);
    }

    [Fact]
    public void NegativeTotal_IsRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ShardPage([], null, -1));
    }

    [Fact]
    public void Entries_AreCopiedInOrder()
    {
        var source = new List<ShardEntry> { Entry("A1"), Entry("B2") };

        var page = new ShardPage(source, null, null);
        source.Reverse();
        source.Add(Entry("C3"));

        page.Entries.Select(e => e.Key.Id.Value).ShouldBe(["A1", "B2"]);
    }

    [Fact]
    public void ArrayEntries_AreCopied()
    {
        ShardEntry[] source = [Entry("A1")];

        var page = new ShardPage(source, null, null);
        source[0] = Entry("Z9");

        page.Entries.ShouldHaveSingleItem().Key.Id.Value.ShouldBe("A1");
    }

    [Fact]
    public void MissingEntries_AreRejected()
    {
        Should.Throw<ArgumentNullException>(() => new ShardPage(null!, null, null)).ParamName.ShouldBe("entries");
        Should.Throw<ArgumentException>(() => new ShardPage([null!], null, null));
    }

    private static ShardEntry Entry(string id) => new(new ResourceKey(East, Patient, ResourceId.Parse(id)), null, null, "{}"u8);
}
