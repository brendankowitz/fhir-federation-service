using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Shards;

public sealed class ShardIdTests
{
    [Theory]
    [InlineData("east")]
    [InlineData("e")]
    [InlineData("East Coast 1")]
    public void Parse_AcceptsTrimmedNonBlankText(string value)
    {
        ShardId.Parse(value).Value.ShouldBe(value);
        ShardId.TryParse(value, out ShardId? id).ShouldBeTrue();
        id!.ToString().ShouldBe(value);
    }

    [Fact]
    public void Parse_AcceptsSixtyFourCharactersAndRejectsSixtyFive()
    {
        ShardId.Parse(new string('s', 64)).Value.Length.ShouldBe(64);
        Should.Throw<ArgumentException>(() => ShardId.Parse(new string('s', 65)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" east")]
    [InlineData("east ")]
    [InlineData("east\n")]
    public void Parse_RejectsBlankOrUntrimmedText(string value)
    {
        Should.Throw<ArgumentException>(() => ShardId.Parse(value));
        ShardId.TryParse(value, out ShardId? id).ShouldBeFalse();
        id.ShouldBeNull();
    }

    [Fact]
    public void Parse_ReportsWhitespaceOnlyAsBlank()
    {
        Should.Throw<ArgumentException>(() => ShardId.Parse("   ")).Message.ShouldContain("must not be blank.");
    }

    [Fact]
    public void TryParse_RejectsNull()
    {
        ShardId.TryParse(null, out _).ShouldBeFalse();
    }

    [Fact]
    public void Equality_IsOrdinalAndCaseSensitive()
    {
        ShardId.Parse("east").ShouldBe(ShardId.Parse("east"));
        ShardId.Parse("east").ShouldNotBe(ShardId.Parse("East"));
    }
}
