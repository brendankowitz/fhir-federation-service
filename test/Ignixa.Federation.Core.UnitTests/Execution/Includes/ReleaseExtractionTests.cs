using Ignixa.Abstractions;
using Ignixa.Federation.Core.Execution.Includes;
using Ignixa.Federation.Core.Identity;
using Ignixa.FhirPath.Parser;
using Ignixa.Specification.Extensions;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Includes.IncludeFixture;

namespace Ignixa.Federation.Core.UnitTests.Execution.Includes;

public sealed class ReleaseExtractionTests
{
    [Fact]
    public void R5Schema_ExtractsReferenceFromR5OnlyResource()
    {
        var entry = Resource(East, ResourceType.Parse("EncounterHistory"), "h1", "1",
            "\"subject\":{\"reference\":\"EncounterHistory/h2\"}");
        var expression = new FhirPathParser(false).Parse("EncounterHistory.subject.where(resolve() is EncounterHistory)");

        IncludeReferenceExtractor.Extract(entry, expression, FhirVersion.R5.GetSchemaProvider()).ShouldBe(["EncounterHistory/h2"]);
    }
}
