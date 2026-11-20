using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Shards;

public sealed class ShardSearchRequestTests
{
    private static readonly ShardId East = ShardId.Parse("east");
    private static readonly ResourceType Observation = ResourceType.Parse("Observation");

    [Fact]
    public void Parameters_KeepOrderDuplicatesAndEncodedText()
    {
        ParameterOccurrence[] occurrences =
        [
            new("code", "a%2Cb"),
            new("code", "x+y"),
            new("name:exact", string.Empty),
            new("code", "a%2Cb"),
            new("date", "ge2020,le2021"),
            new("value", "%2b5"),
        ];

        var request = new ShardSearchRequest(East, Observation, occurrences, 20, ShardSearchMethod.Get);

        request.Parameters.ShouldBe(occurrences);
        request.Shard.ShouldBe(East);
        request.Type.ShouldBe(Observation);
        request.PageSize.ShouldBe(20);
        request.Method.ShouldBe(ShardSearchMethod.Get);
    }

    [Fact]
    public void Parameters_AreCopiedAndReadOnly()
    {
        var source = new List<ParameterOccurrence> { new("code", "a") };

        var request = new ShardSearchRequest(East, Observation, source, 20, ShardSearchMethod.PostSearch);
        source.Add(new ParameterOccurrence("code", "b"));
        source[0] = new ParameterOccurrence("code", "z");

        request.Parameters.ShouldBe([new ParameterOccurrence("code", "a")]);
        Should.Throw<NotSupportedException>(() => ((ICollection<ParameterOccurrence>)request.Parameters).Add(new("x", "y")));
    }

    [Fact]
    public void ArrayParameters_AreCopied()
    {
        ParameterOccurrence[] source = [new("code", "a")];

        var request = new ShardSearchRequest(East, Observation, source, 20, ShardSearchMethod.Get);
        source[0] = new ParameterOccurrence("code", "z");

        request.Parameters.ShouldBe([new ParameterOccurrence("code", "a")]);
    }

    // FHIR parameter names are case-sensitive: only the exact name collides with the page size.
    [Fact]
    public void OtherSpellingsOfCount_AreOrdinaryParameters()
    {
        new ShardSearchRequest(East, Observation, [new("_Count", "10")], 20, ShardSearchMethod.Get)
            .Parameters.ShouldBe([new ParameterOccurrence("_Count", "10")]);
    }

    [Fact]
    public void PageSizeZero_IsCountOnly_AndNegativeIsRejected()
    {
        new ShardSearchRequest(East, Observation, [], 0, ShardSearchMethod.Get).PageSize.ShouldBe(0);

        Should.Throw<ArgumentOutOfRangeException>(() => new ShardSearchRequest(East, Observation, [], -1, ShardSearchMethod.Get));
    }

    [Fact]
    public void CountOccurrence_IsRejectedBecausePageSizeOwnsIt()
    {
        Should.Throw<ArgumentException>(
            () => new ShardSearchRequest(East, Observation, [new("code", "a"), new("_count", "10")], 20, ShardSearchMethod.Get));
    }

    [Fact]
    public void UndefinedMethod_IsRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ShardSearchRequest(East, Observation, [], 20, (ShardSearchMethod)7));
    }

    [Fact]
    public void MissingParts_AreRejected()
    {
        Should.Throw<ArgumentNullException>(() => new ShardSearchRequest(null!, Observation, [], 20, ShardSearchMethod.Get));
        Should.Throw<ArgumentNullException>(() => new ShardSearchRequest(East, null!, [], 20, ShardSearchMethod.Get));
        Should.Throw<ArgumentNullException>(() => new ShardSearchRequest(East, Observation, null!, 20, ShardSearchMethod.Get))
            .ParamName.ShouldBe("parameters");
        Should.Throw<ArgumentException>(() => new ShardSearchRequest(East, Observation, [null!], 20, ShardSearchMethod.Get));
    }
}
