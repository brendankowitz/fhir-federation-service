using Ignixa.Federation.Shards.Capabilities;
using Shouldly;
using static Ignixa.Federation.Shards.UnitTests.Capabilities.CapabilityJson;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities;

/// <summary>Software evidence comes only from <c>software.name</c> and <c>software.version</c>, and is never completed or invented.</summary>
public sealed class CapabilitySoftwareTests
{
    private static SoftwareEvidence? Software(string extraRoot) => Read(Statement(extraRoot: extraRoot)).Software;

    [Fact]
    public void NameAndVersion_AreKeptExactly()
    {
        SoftwareEvidence? software = Software(""","software":{"name":" Microsoft FHIR Server ","version":"v2.0.1-beta+Build.7","releaseDate":"2026-01-01"}""");

        software.ShouldNotBeNull();
        software.Name.ShouldBe(" Microsoft FHIR Server ");
        software.Version.ShouldBe("v2.0.1-beta+Build.7");
    }

    [Fact]
    public void NoSoftwareBlock_IsNoEvidenceNotAnIdentity()
    {
        Software(string.Empty).ShouldBeNull();
    }

    [Fact]
    public void ABlockReportingNeitherPart_IsNoEvidence()
    {
        Software(""","software":{"releaseDate":"2026-01-01"}""").ShouldBeNull();
        Software(""","software":{}""").ShouldBeNull();
    }

    [Fact]
    public void ANameWithoutAVersion_IsPartialNotComplete()
    {
        SoftwareEvidence? software = Software(""","software":{"name":"Ignixa"}""");

        software.ShouldNotBeNull();
        software.Name.ShouldBe("Ignixa");
        software.Version.ShouldBeNull();
    }

    [Fact]
    public void AVersionWithoutAName_IsPartialNotComplete()
    {
        SoftwareEvidence? software = Software(""","software":{"version":"1.2.3"}""");

        software.ShouldNotBeNull();
        software.Name.ShouldBeNull();
        software.Version.ShouldBe("1.2.3");
    }

    [Fact]
    public void RootNameVersionTitleAndPublisher_AreNeverSoftware()
    {
        SoftwareEvidence? software = Software(
            ""","name":"RootName","version":"9.9.9","title":"Some Title","publisher":"Some Publisher","implementation":{"description":"Impl Name","url":"https://example.org/fhir"}""");

        software.ShouldBeNull();
    }

    [Fact]
    public void RootIdentifiers_DoNotCompleteAPartialSoftwareBlock()
    {
        SoftwareEvidence? software = Software(""","name":"RootName","version":"9.9.9","software":{"name":"Reported"}""");

        software.ShouldNotBeNull();
        software.Name.ShouldBe("Reported");
        software.Version.ShouldBeNull();
    }

    [Fact]
    public void SoftwareIsReadEvenWhenTheStatementAdvertisesNoServer()
    {
        Read(Statement(Arr("""{"mode":"client"}"""), ""","software":{"name":"Ignixa","version":"1"}""")).Software
            .ShouldNotBeNull().Name.ShouldBe("Ignixa");
    }

    [Theory]
    [InlineData("""{"name":5}""")]
    [InlineData("""{"name":""}""")]
    [InlineData("""{"name":"   "}""")]
    [InlineData("""{"name":null}""")]
    [InlineData("""{"name":["a"]}""")]
    [InlineData("""{"name":"ok","version":7}""")]
    [InlineData("""{"name":"ok","version":""}""")]
    [InlineData("""{"name":"ok","version":null}""")]
    [InlineData("""{"name":"ok","version":"\ud800"}""")]
    [InlineData("""{"version":"  "}""")]
    [InlineData("""[]""")]
    [InlineData(""""Ignixa"""")]
    [InlineData("""null""")]
    [InlineData("""5""")]
    public void MalformedSoftware_IsRefusedNotReducedToAbsent(string software)
    {
        Should.Throw<FormatException>(() => Software($$""","software":{{software}}"""));
    }
}
