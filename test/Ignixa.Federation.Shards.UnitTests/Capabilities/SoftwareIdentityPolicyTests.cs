using System.Text.Json;
using Ignixa.Federation.Shards.Capabilities;
using Shouldly;
using static Ignixa.Federation.Shards.UnitTests.Capabilities.CapabilityJson;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities;

public sealed class SoftwareIdentityPolicyTests
{
    private const string Product = "Synthetic FHIR";

    [Theory]
    [InlineData("1.2.3-rc.2+Build.7", SoftwareIdentityMatch.Matched)]
    [InlineData("1.2.3-rc.2+Build.8", SoftwareIdentityMatch.VersionMismatch)]
    [InlineData("1.2.3-rc.2+build.7", SoftwareIdentityMatch.VersionMismatch)]
    [InlineData("1.2.3-rc.3+Build.7", SoftwareIdentityMatch.VersionMismatch)]
    [InlineData("1.2.3-rc.2", SoftwareIdentityMatch.VersionMismatch)]
    [InlineData("1.2.3", SoftwareIdentityMatch.VersionMismatch)]
    public void ExactPolicyDistinguishesTheWholeAdvertisedIdentity(string version, SoftwareIdentityMatch expected)
    {
        SoftwareIdentityPolicy policy = SoftwareIdentityPolicy.Exact(Product, "1.2.3-rc.2+Build.7");

        policy.Match(Advertised(version: version)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("1.2.3", SoftwareIdentityMatch.Matched)]
    [InlineData("1.2.4", SoftwareIdentityMatch.VersionMismatch)]
    [InlineData("1.2.3+build", SoftwareIdentityMatch.VersionMismatch)]
    public void ExactPlainReleaseDoesNotAllowAnotherPatchOrAnUnlistedBuild(string version, SoftwareIdentityMatch expected)
    {
        SoftwareIdentityPolicy.Exact(Product, "1.2.3").Match(Advertised(version: version)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("1.1.99", SoftwareIdentityMatch.VersionMismatch)]
    [InlineData("1.2.0", SoftwareIdentityMatch.Matched)]
    [InlineData("1.9.9", SoftwareIdentityMatch.Matched)]
    [InlineData("1.10.0", SoftwareIdentityMatch.VersionMismatch)]
    [InlineData("1.2.1-rc.1", SoftwareIdentityMatch.VersionMismatch)]
    [InlineData("1.2.1+Build.2", SoftwareIdentityMatch.VersionMismatch)]
    [InlineData("1.2.2147483648", SoftwareIdentityMatch.VersionMismatch)]
    public void RangeIsNumericLowerInclusiveUpperExclusiveAndOnlyForPlainReleases(string version, SoftwareIdentityMatch expected)
    {
        SoftwareIdentityPolicy policy = SoftwareIdentityPolicy.ReleaseRange(Product, "1.2.0", "1.10.0");

        policy.Match(Advertised(version: version)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("synthetic FHIR")]
    [InlineData("Synthetic FHIR Cloud")]
    public void SimilarProductNamesDoNotMatch(string name)
    {
        SoftwareIdentityPolicy.Exact(Product, "1.2.3")
            .Match(Advertised(name: name)).ShouldBe(SoftwareIdentityMatch.ProductMismatch);
    }

    [Theory]
    [InlineData("")]
    [InlineData(""","software":{"name":"Synthetic FHIR"}""")]
    [InlineData(""","software":{"version":"1.2.3"}""")]
    public void RootIdentifiersNeverFillInMissingSoftwareFields(string software)
    {
        ShardCapabilityEvidence advertised = Read(Statement(extraRoot: ",\"name\":\"Synthetic FHIR\",\"version\":\"1.2.3\"" + software));

        SoftwareIdentityPolicy.Exact(Product, "1.2.3").Match(advertised).ShouldBe(SoftwareIdentityMatch.MissingIdentity);
    }

    [Theory]
    [InlineData("v1.2.3")]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.2.+3")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3+bad..metadata")]
    [InlineData("1.2.3\n")]
    public void MalformedVersionsAreNotNormalizedOrMatched(string version)
    {
        SoftwareIdentityPolicy.Exact(Product, "1.2.3").Match(Advertised(version: version))
            .ShouldBe(SoftwareIdentityMatch.MalformedIdentity);
        Should.Throw<ArgumentException>(() => SoftwareIdentityPolicy.Exact(Product, version));
    }

    [Fact]
    public void ADeclaredExactBuildMayContainNumericBuildIdentifiersWithLeadingZeros()
    {
        SoftwareIdentityPolicy.Exact(Product, "1.2.3+0001").Match(Advertised(version: "1.2.3+0001"))
            .ShouldBe(SoftwareIdentityMatch.Matched);
    }

    [Fact]
    public void MalformedProductNamesAreRefusedInEvidenceAndPolicy()
    {
        SoftwareIdentityPolicy.Exact(Product, "1.2.3").Match(Advertised(name: Product + "\t"))
            .ShouldBe(SoftwareIdentityMatch.MalformedIdentity);
        Should.Throw<ArgumentException>(() => SoftwareIdentityPolicy.Exact(Product + "\t", "1.2.3"));
    }

    [Theory]
    [InlineData("1.10.0", "1.2.0")]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("1.2.0-rc.1", "1.10.0")]
    [InlineData("1.2.0", "1.10.0+build")]
    [InlineData("1.2.0", "1.10.2147483648")]
    public void InvalidOrEmptyRangeIsRejectedWhenThePolicyIsCreated(string lower, string upper)
    {
        Should.Throw<ArgumentException>(() => SoftwareIdentityPolicy.ReleaseRange(Product, lower, upper));
    }

    private static ShardCapabilityEvidence Advertised(string name = Product, string version = "1.2.3") =>
        Read(Statement(extraRoot: ",\"software\":" + JsonSerializer.Serialize(new { name, version })));
}
