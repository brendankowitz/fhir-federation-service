using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Search;

public sealed class ReferenceClassifierTests
{
    private static readonly ShardId East = ShardId.Parse("east");
    private static readonly ShardId West = ShardId.Parse("west");
    private static readonly ShardId North = ShardId.Parse("north");
    private static readonly ShardId Root = ShardId.Parse("root");

    // east:  https://east.example/fhir             (alias https://east-private.example:8443/fhir)
    // west:  https://west.example/fhir
    // north: https://north.example/fhir            (alias nested on a segment boundary: https://north.example/fhir/v2)
    // root:  https://root.example                  (base with no path)
    private static readonly ShardRegistry Registry = TestShards.Create(
        TestShards.East("https://east-private.example:8443/fhir/"),
        TestShards.West(),
        TestShards.Shard("north", "n3rt", "https://north.example/fhir", "https://north.example/fhir/v2"),
        TestShards.Shard("root", "r0ot", "https://root.example/"));

    private static readonly ReferenceClassifier Classifier = new(Registry);

    private static ReferenceClassification Classify(string? reference, ShardId? containing = null) =>
        Classifier.Classify(containing ?? East, reference);

    private static void ShouldResolveTo(ReferenceClassification result, ReferenceKind kind, ShardId shard, string type, string id, string? version = null)
    {
        result.Kind.ShouldBe(kind);
        result.Target.ShouldBe(new ResourceKey(shard, ResourceType.Parse(type), ResourceId.Parse(id)));
        result.Version.ShouldBe(version);
    }

    private static void ShouldNotResolve(ReferenceClassification result, ReferenceKind kind)
    {
        result.Kind.ShouldBe(kind);
        result.Target.ShouldBeNull();
        result.Version.ShouldBeNull();
    }

    // --- relative ----------------------------------------------------------------------------------------------

    [Fact]
    public void RelativeReferenceBelongsToTheContainingShard()
    {
        ShouldResolveTo(Classify("Patient/123", West), ReferenceKind.Relative, West, "Patient", "123");
        ShouldResolveTo(Classify("Patient/123", East), ReferenceKind.Relative, East, "Patient", "123");
    }

    [Fact]
    public void SameRelativeReferenceOnTwoShardsIsTwoDistinctKeys()
    {
        Classify("Patient/123", East).Target.ShouldNotBe(Classify("Patient/123", West).Target);
    }

    [Fact]
    public void VersionedRelativeReferenceKeepsItsVersionOnTheContainingShard()
    {
        ShouldResolveTo(
            Classify("Patient/p1/_history/7", West),
            ReferenceKind.Relative,
            West,
            "Patient",
            "p1",
            "7");
    }

    [Fact]
    public void ReferenceIsKeptVerbatim()
    {
        Classify("Patient/p1/_history/7").Literal.ShouldBe("Patient/p1/_history/7");
        Classify("https://other.example/x").Literal.ShouldBe("https://other.example/x");
        Classify("Patient/%31").Literal.ShouldBe("Patient/%31");
        Classify(null).Literal.ShouldBeNull();
    }

    [Theory]
    [InlineData("Patient/Abc", "Abc")]
    [InlineData("Patient/abc", "abc")]
    [InlineData("Patient/a.b-C", "a.b-C")]
    [InlineData("Patient/.", ".")]
    [InlineData("Patient/..", "..")]
    public void IdsAreCaseSensitiveAndMayBeDotsLikeR4(string reference, string id)
    {
        ShouldResolveTo(Classify(reference), ReferenceKind.Relative, East, "Patient", id);
    }

    [Fact]
    public void IdMayBeSixtyFourCharactersButNotSixtyFive()
    {
        ShouldResolveTo(Classify("Patient/" + new string('a', 64)), ReferenceKind.Relative, East, "Patient", new string('a', 64));
        ShouldNotResolve(Classify("Patient/" + new string('a', 65)), ReferenceKind.Invalid);
    }

    [Fact]
    public void VersionFollowsTheIdRules()
    {
        ShouldResolveTo(Classify("Patient/p/_history/V1.a-2"), ReferenceKind.Relative, East, "Patient", "p", "V1.a-2");
        ShouldNotResolve(Classify("Patient/p/_history/" + new string('9', 65)), ReferenceKind.Invalid);
        ShouldNotResolve(Classify("Patient/p/_history/"), ReferenceKind.Invalid);
        ShouldNotResolve(Classify("Patient/p/_history/1/2"), ReferenceKind.Invalid);
        ShouldNotResolve(Classify("Patient/p/_history/a_b"), ReferenceKind.Invalid);
    }

    [Theory]
    [InlineData("Patient/p/_History/1")]
    [InlineData("Patient/p/_HISTORY/1")]
    [InlineData("Patient/p/history/1")]
    [InlineData("Patient/p/_history")]
    public void HistoryMarkerIsCaseSensitiveAndComplete(string reference)
    {
        ShouldNotResolve(Classify(reference), ReferenceKind.Invalid);
    }

    [Fact]
    public void TypeIsOnlyCheckedForShapeNotAgainstTheR4TypeList()
    {
        ShouldResolveTo(Classify("Patient/1"), ReferenceKind.Relative, East, "Patient", "1");
        ShouldResolveTo(Classify("Widget/1"), ReferenceKind.Relative, East, "Widget", "1");
    }

    [Theory]
    [InlineData("Pat1ent/1")]
    [InlineData("Patient_/1")]
    public void TypeIsCaseSensitiveAndLettersOnly(string reference)
    {
        ShouldNotResolve(Classify(reference), ReferenceKind.Invalid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Patient")]
    [InlineData("Patient/")]
    [InlineData("/1")]
    [InlineData("/Patient/1")]
    [InlineData("//east.example/fhir/Patient/1")]
    [InlineData("Patient//1")]
    [InlineData("Patient/1/")]
    [InlineData("Patient/1/extra")]
    [InlineData("Patient/1/_history/2/extra")]
    [InlineData(" Patient/1")]
    [InlineData("Patient/1 ")]
    [InlineData("Patient/1\n")]
    [InlineData("Patient/1\0")]
    [InlineData("Patient\\1")]
    [InlineData("Patient/%31")]
    [InlineData("Patient/a_b")]
    [InlineData("Patient/é")]
    [InlineData("Patient/1?x=y")]
    [InlineData("Patient/1#frag")]
    [InlineData("Patient?identifier=1")]
    [InlineData("../Patient/1")]
    [InlineData("./Patient/1")]
    [InlineData("Patient/../Patient/1")]
    public void MalformedRelativeFormsAreInvalidNotGuessed(string reference)
    {
        ShouldNotResolve(Classify(reference), ReferenceKind.Invalid);
    }

    // --- known absolute (registered base and aliases) -----------------------------------------------------------

    [Fact]
    public void AbsoluteReferenceToAnotherShardsBaseRoutesToThatShard()
    {
        ShouldResolveTo(Classify("https://west.example/fhir/Patient/p2", East), ReferenceKind.KnownAbsolute, West, "Patient", "p2");
    }

    [Fact]
    public void AbsoluteReferenceToTheContainingShardsOwnBaseStaysOnItsShard()
    {
        ShouldResolveTo(Classify("https://east.example/fhir/Patient/p2", East), ReferenceKind.KnownAbsolute, East, "Patient", "p2");
    }

    [Fact]
    public void AbsoluteReferenceIgnoresTheContainingShardEvenWhenItNamesOtherShard()
    {
        ShouldResolveTo(Classify("https://west.example/fhir/Patient/p2", North), ReferenceKind.KnownAbsolute, West, "Patient", "p2");
    }

    [Fact]
    public void AbsoluteReferenceKeepsTheVersion()
    {
        ShouldResolveTo(
            Classify("https://west.example/fhir/Observation/o1/_history/3"),
            ReferenceKind.KnownAbsolute,
            West,
            "Observation",
            "o1",
            "3");
    }

    [Theory]
    [InlineData("https://east-private.example:8443/fhir/Patient/p1")]
    [InlineData("https://EAST-PRIVATE.Example:8443/fhir/Patient/p1")]
    [InlineData("HTTPS://east-private.example:8443/fhir/Patient/p1")]
    public void LinkBaseAliasRoutesToItsShardWithSchemeAndHostCaseFolded(string reference)
    {
        ShouldResolveTo(Classify(reference, West), ReferenceKind.KnownAbsolute, East, "Patient", "p1");
    }

    [Theory]
    [InlineData("https://EAST.example/fhir/Patient/p1")]
    [InlineData("HTTPS://east.example/fhir/Patient/p1")]
    [InlineData("https://east.example:443/fhir/Patient/p1")]
    [InlineData("https://east.example:0443/fhir/Patient/p1")]
    public void SchemeHostAndDefaultPortAreNormalisedLikeTheRegisteredBase(string reference)
    {
        ShouldResolveTo(Classify(reference, West), ReferenceKind.KnownAbsolute, East, "Patient", "p1");
    }

    [Fact]
    public void PathCaseIsSignificantOnTheBase()
    {
        ShouldNotResolve(Classify("https://east.example/FHIR/Patient/p1"), ReferenceKind.ForeignAbsolute);
    }

    [Fact]
    public void RegisteredPortIsPartOfTheBase()
    {
        ShouldNotResolve(Classify("https://east-private.example/fhir/Patient/p1"), ReferenceKind.ForeignAbsolute);
        ShouldNotResolve(Classify("https://east-private.example:9443/fhir/Patient/p1"), ReferenceKind.ForeignAbsolute);
        ShouldNotResolve(Classify("https://east.example:8443/fhir/Patient/p1"), ReferenceKind.ForeignAbsolute);
    }

    [Fact]
    public void PlainHttpIsNotTheRegisteredHttpsBase()
    {
        ShouldNotResolve(Classify("http://east.example/fhir/Patient/p1"), ReferenceKind.ForeignAbsolute);
    }

    [Fact]
    public void BaseWithoutPathMatchesItsHostRoot()
    {
        ShouldResolveTo(Classify("https://root.example/Patient/p1", East), ReferenceKind.KnownAbsolute, Root, "Patient", "p1");
        ShouldNotResolve(Classify("https://root.example/fhir/Patient/p1", East), ReferenceKind.Invalid);
    }

    [Fact]
    public void NestedAliasUsesTheLongestBaseOnASegmentBoundary()
    {
        ShouldResolveTo(Classify("https://north.example/fhir/v2/Patient/p1"), ReferenceKind.KnownAbsolute, North, "Patient", "p1");
        ShouldResolveTo(Classify("https://north.example/fhir/Patient/p1"), ReferenceKind.KnownAbsolute, North, "Patient", "p1");
    }

    [Fact]
    public void BaseMatchesOnWholeSegmentsOnly()
    {
        ShouldNotResolve(Classify("https://east.example/fhirx/Patient/p1"), ReferenceKind.ForeignAbsolute);
        ShouldNotResolve(Classify("https://east.example/fhi/Patient/p1"), ReferenceKind.ForeignAbsolute);
        ShouldNotResolve(Classify("https://east.example/Patient/p1"), ReferenceKind.ForeignAbsolute);
    }

    [Fact]
    public void FullUrlOfAKeyClassifiesBackToTheSameKey()
    {
        foreach (string id in new[] { "p1", "A1", ".", ".." })
        {
            foreach (ShardId shard in new[] { East, West, North, Root })
            {
                var key = new ResourceKey(shard, ResourceType.Parse("Patient"), ResourceId.Parse(id));

                Classify(Registry.FullUrl(key), West).Target.ShouldBe(key);
            }
        }
    }

    [Theory]
    [InlineData("https://east.example/fhir/Patient")]
    [InlineData("https://east.example/fhir")]
    [InlineData("https://east.example/fhir/")]
    [InlineData("https://east.example/fhir/metadata")]
    [InlineData("https://east.example/fhir/Patient/")]
    [InlineData("https://east.example/fhir/Patient/p1/")]
    [InlineData("https://east.example/fhir/Patient/p1/$everything")]
    [InlineData("https://east.example/fhir/patient/p1")]
    [InlineData("https://east.example/fhir/Patient//p1")]
    [InlineData("https://east.example/fhir/Patient/p1/_history")]
    [InlineData("https://east.example/fhir/Patient/p1?_format=json")]
    [InlineData("https://east.example/fhir/Patient/p1#x")]
    public void RegisteredBaseThatIsNotAResourceUrlIsInvalidNotRouted(string reference)
    {
        ShouldNotResolve(Classify(reference), ReferenceKind.Invalid);
    }

    [Theory]
    [InlineData("https://east.example/other/../fhir/Patient/p1")]
    [InlineData("https://east.example/fhir/../fhir/Patient/p1")]
    [InlineData("https://east.example/./fhir/Patient/p1")]
    [InlineData("https://east.example//fhir/Patient/p1")]
    [InlineData("https://east.example/fhir//Patient/p1")]
    [InlineData("https://east.example/%66hir/Patient/p1")]
    [InlineData("https://east.example/fhir/%50atient/p1")]
    [InlineData("https://east.example/fhir/Patient/%70%31")]
    [InlineData("https://east.example/fhir/%2e%2e/fhir/Patient/p1")]
    [InlineData("https://east.example\\fhir\\Patient\\p1")]
    [InlineData("https://east.example/fhir\\Patient/p1")]
    [InlineData("https://user@east.example/fhir/Patient/p1")]
    [InlineData("https://east.example@evil.example/fhir/Patient/p1")]
    [InlineData("https://east.example:/fhir/Patient/p1")]
    [InlineData("https://east.example:99999/fhir/Patient/p1")]
    [InlineData("https://east.example:abc/fhir/Patient/p1")]
    [InlineData("https:east.example/fhir/Patient/p1")]
    [InlineData("https:/east.example/fhir/Patient/p1")]
    [InlineData("https:///east.example/fhir/Patient/p1")]
    [InlineData("https://")]
    [InlineData("https://east.example/fhir/Patient/p1 ")]
    public void UriNormalisedForeignLookingFormsNeverBecomeLocal(string reference)
    {
        ShouldNotResolve(Classify(reference), ReferenceKind.Invalid);
    }

    [Fact]
    public void EscapedBaseCharactersCompareWithUppercaseHexLikeTheRegistry()
    {
        ShardRegistry escaped = TestShards.Create(TestShards.Shard("east", "k7q2", "https://east.example/a%2fb"));
        var classifier = new ReferenceClassifier(escaped);

        classifier.Classify(East, "https://east.example/a%2Fb/Patient/p1").Target.ShouldNotBeNull().Shard.ShouldBe(East);
        classifier.Classify(East, "https://east.example/a%2fb/Patient/p1").Target.ShouldNotBeNull().Shard.ShouldBe(East);
        classifier.Classify(East, "https://east.example/a/b/Patient/p1").Kind.ShouldBe(ReferenceKind.ForeignAbsolute);
    }

    // --- IPv6 bases -----------------------------------------------------------------------------------------------

    private static readonly ShardId V6 = ShardId.Parse("v6");

    // ShardBaseUrl keeps the brackets of an IPv6 literal and lets Uri compress and lowercase it.
    private static readonly ShardRegistry V6Registry = TestShards.Create(
        TestShards.East(),
        TestShards.Shard("v6", "v6xx", "https://[2001:DB8:0:0:0:0:0:1]:8443/fhir", "https://[::1]/fhir"));

    private static readonly ReferenceClassifier V6Classifier = new(V6Registry);

    [Fact]
    public void FullUrlOfAnIpv6BaseClassifiesBackToTheSameKey()
    {
        var key = new ResourceKey(V6, ResourceType.Parse("Patient"), ResourceId.Parse("p1"));

        V6Classifier.Classify(East, V6Registry.FullUrl(key)).Target.ShouldBe(key);
    }

    [Theory]
    [InlineData("https://[2001:db8::1]:8443/fhir/Patient/p1")]
    [InlineData("https://[2001:DB8::1]:8443/fhir/Patient/p1")]
    [InlineData("https://[2001:db8:0:0:0:0:0:1]:8443/fhir/Patient/p1")]
    [InlineData("https://[::1]/fhir/Patient/p1")]
    [InlineData("https://[::1]:443/fhir/Patient/p1")]
    [InlineData("https://[0:0:0:0:0:0:0:1]/fhir/Patient/p1")]
    [InlineData("HTTPS://[::1]/fhir/Patient/p1")]
    public void Ipv6BaseAndAliasMatchInAnyEquivalentLiteralSpelling(string reference)
    {
        V6Classifier.Classify(East, reference).ShouldSatisfyAllConditions(
            r => r.Kind.ShouldBe(ReferenceKind.KnownAbsolute),
            r => r.Target.ShouldBe(new ResourceKey(V6, ResourceType.Parse("Patient"), ResourceId.Parse("p1"))));
    }

    [Theory]
    [InlineData("https://[::1/fhir/Patient/p1")]
    [InlineData("https://[::1]x/fhir/Patient/p1")]
    [InlineData("https://[::1]:/fhir/Patient/p1")]
    [InlineData("https://[::1]:99999/fhir/Patient/p1")]
    [InlineData("https://[::2::1]/fhir/Patient/p1")]
    [InlineData("https://[fe80::1%25eth0]/fhir/Patient/p1")]
    [InlineData("https://[::1]@evil.example/fhir/Patient/p1")]
    [InlineData("https://::1/fhir/Patient/p1")]
    public void MalformedIpv6AuthorityIsInvalid(string reference)
    {
        V6Classifier.Classify(East, reference).Kind.ShouldBe(ReferenceKind.Invalid);
    }

    [Fact]
    public void Ipv6BaseDoesNotMatchAnotherAddressOrPort()
    {
        V6Classifier.Classify(East, "https://[::2]/fhir/Patient/p1").Kind.ShouldBe(ReferenceKind.ForeignAbsolute);
        V6Classifier.Classify(East, "https://[2001:db8::1]/fhir/Patient/p1").Kind.ShouldBe(ReferenceKind.ForeignAbsolute);
    }

    // --- escaped base text ---------------------------------------------------------------------------------------------

    [Fact]
    public void EscapedUnreservedTextInABaseMatchesOnlyItsCanonicalSpelling()
    {
        // The registry decodes escaped unreserved characters (%41 -> A). A reference must use that canonical spelling:
        // decoding it here would also have to decode the resource tail (Patient/%31 -> Patient/1), which would change a
        // resource's identity, so an escaped spelling is not trusted and is never routed.
        ShardRegistry escaped = TestShards.Create(TestShards.Shard("east", "k7q2", "https://east.example/%41bc"));
        var classifier = new ReferenceClassifier(escaped);

        classifier.Classify(East, "https://east.example/Abc/Patient/p1").Kind.ShouldBe(ReferenceKind.KnownAbsolute);
        classifier.Classify(East, escaped.FullUrl(new ResourceKey(East, ResourceType.Parse("Patient"), ResourceId.Parse("p1"))))
            .Kind.ShouldBe(ReferenceKind.KnownAbsolute);
        classifier.Classify(East, "https://east.example/%41bc/Patient/p1").Kind.ShouldBe(ReferenceKind.Invalid);
        classifier.Classify(East, "https://east.example/Abc/Patient/%70%31").Kind.ShouldBe(ReferenceKind.Invalid);
    }

    // --- foreign absolute --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://other.example/fhir/Patient/p1")]
    [InlineData("https://west.example.evil.example/fhir/Patient/p1")]
    [InlineData("https://evil.example/west.example/fhir/Patient/p1")]
    [InlineData("https://west.example/fhir2/Patient/p1")]
    [InlineData("http://other.example/Patient/p1")]
    [InlineData("ftp://other.example/Patient/p1")]
    [InlineData("urn:uuid:53fefa32-fcbb-4ff8-8a92-55ee120877b7")]
    [InlineData("urn:oid:1.2.3")]
    [InlineData("https://other.example/Patient")]
    public void AbsoluteReferenceToUnregisteredServerIsForeign(string reference)
    {
        ShouldNotResolve(Classify(reference), ReferenceKind.ForeignAbsolute);
    }

    // --- contained, identifier-only, absent ---------------------------------------------------------------------

    [Theory]
    [InlineData("#p1")]
    [InlineData("#")]
    [InlineData("#a.b_c")]
    public void ContainedReferenceIsNeverRouted(string reference)
    {
        ShouldNotResolve(Classify(reference), ReferenceKind.Contained);
    }

    [Fact]
    public void ReferenceWithoutLiteralIsIdentifierOnly()
    {
        ShouldNotResolve(Classify(null), ReferenceKind.IdentifierOnly);
    }

    // --- boundary ----------------------------------------------------------------------------------------------

    [Fact]
    public void ContainingShardMustBeInTheRegistry()
    {
        Should.Throw<ArgumentException>(() => Classifier.Classify(ShardId.Parse("elsewhere"), "Patient/1"));
        Should.Throw<ArgumentNullException>(() => Classifier.Classify(null!, "Patient/1"));
        Should.Throw<ArgumentNullException>(() => new ReferenceClassifier(null!));
    }
}
