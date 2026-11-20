using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Shards;

public sealed class ShardAliasTests
{
    [Theory]
    [InlineData("k7q2")]
    [InlineData("0")]
    [InlineData("a_b-c")]
    public void Parse_AcceptsLowercaseRouteSegments(string value)
    {
        ShardAlias.Parse(value).Value.ShouldBe(value);
        ShardAlias.TryParse(value, out ShardAlias? alias).ShouldBeTrue();
        alias!.ToString().ShouldBe(value);
    }

    [Fact]
    public void Parse_AcceptsThirtyTwoCharactersAndRejectsThirtyThree()
    {
        ShardAlias.Parse(new string('a', 32)).Value.Length.ShouldBe(32);
        Should.Throw<ArgumentException>(() => ShardAlias.Parse(new string('a', 33)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("K7Q2")]
    [InlineData("k7Q2")]
    [InlineData("-a")]
    [InlineData("_a")]
    [InlineData("a.b")]
    [InlineData("k7q2\n")]
    [InlineData("\u00e4")]
    public void Parse_RejectsRatherThanNormalises(string value)
    {
        Should.Throw<ArgumentException>(() => ShardAlias.Parse(value));
        ShardAlias.TryParse(value, out ShardAlias? alias).ShouldBeFalse();
        alias.ShouldBeNull();
    }

    [Fact]
    public void TryParse_RejectsNull()
    {
        ShardAlias.TryParse(null, out _).ShouldBeFalse();
    }

    [Fact]
    public void Equality_IsByValue()
    {
        ShardAlias.Parse("k7q2").ShouldBe(ShardAlias.Parse("k7q2"));
        ShardAlias.Parse("k7q2").ShouldNotBe(ShardAlias.Parse("p9xm"));
    }
}
