using Ignixa.Federation.Core.Search;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Search.TestSearch;

namespace Ignixa.Federation.Core.UnitTests.Search;

public sealed class ReferencePathCertificationTests
{
    [Fact]
    public void FormatsAsTheConfiguredPassthroughSpelling()
    {
        ObservationSubjectPatient.ToString().ShouldBe("Observation.subject:Patient");
    }

    [Theory]
    [InlineData("")]
    [InlineData("sub ject")]
    [InlineData("subject:Patient")]
    [InlineData("subject.name")]
    public void RejectsAReferenceCodeThatIsNotASearchCode(string code)
    {
        Should.Throw<ArgumentException>(() => new ReferencePath(Type("Observation"), code, Type("Patient")));
    }

    [Fact]
    public void RejectsDistributedForwardWithoutOwnSideForms()
    {
        Should.Throw<ArgumentException>(() => new ReferencePathCertification(
            ObservationSubjectPatient,
            ReferencePathOperations.DistributedForward,
            ReferenceSearchForms.None,
            ReferenceSearchForms.CanonicalAbsolute));
    }

    [Fact]
    public void RejectsAForeignFormPolicyWithoutCanonicalAbsolute()
    {
        Should.Throw<ArgumentException>(() => new ReferencePathCertification(
            ObservationSubjectPatient,
            ReferencePathOperations.RevIncludeSearch,
            ReferenceSearchForms.Relative,
            ReferenceSearchForms.LinkBaseAliases));
    }

    // Relative notation names the shard's own resource; it cannot stand for a foreign one.
    [Fact]
    public void RejectsAForeignFormPolicyThatMatchesRelativeReferences()
    {
        Should.Throw<ArgumentException>(() => new ReferencePathCertification(
            ObservationSubjectPatient,
            ReferencePathOperations.DistributedForward,
            ReferenceSearchForms.Relative,
            ReferenceSearchForms.CanonicalAbsolute | ReferenceSearchForms.Relative));
    }

    [Fact]
    public void RejectsFormsForOperationsThatSearchNoReferences()
    {
        Should.Throw<ArgumentException>(() => new ReferencePathCertification(
            ObservationSubjectPatient,
            ReferencePathOperations.NativeForward | ReferencePathOperations.IncludeExtraction,
            ReferenceSearchForms.Relative,
            ReferenceSearchForms.None));
    }

    [Fact]
    public void AcceptsNativeOperationsWithoutForms()
    {
        var certification = new ReferencePathCertification(
            ObservationSubjectPatient,
            ReferencePathOperations.NativeForward | ReferencePathOperations.NativeReverse,
            ReferenceSearchForms.None,
            ReferenceSearchForms.None);

        certification.Operations.ShouldBe(ReferencePathOperations.NativeForward | ReferencePathOperations.NativeReverse);
    }

    [Fact]
    public void RejectsACertificationThatProvesNothing()
    {
        Should.Throw<ArgumentException>(() => new ReferencePathCertification(
            ObservationSubjectPatient,
            ReferencePathOperations.None,
            ReferenceSearchForms.None,
            ReferenceSearchForms.None));
    }
}
