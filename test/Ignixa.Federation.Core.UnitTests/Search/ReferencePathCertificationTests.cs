using Ignixa.Federation.Core.Search;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Search.TestSearch;

namespace Ignixa.Federation.Core.UnitTests.Search;

public sealed class ReferencePathCertificationTests
{
    private static readonly string Covered = Registry().Version;

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
            ReferenceSearchForms.CanonicalAbsolute,
            Covered));
    }

    [Fact]
    public void RejectsAForeignFormPolicyWithoutCanonicalAbsolute()
    {
        Should.Throw<ArgumentException>(() => new ReferencePathCertification(
            ObservationSubjectPatient,
            ReferencePathOperations.RevIncludeSearch,
            ReferenceSearchForms.Relative,
            ReferenceSearchForms.LinkBaseAliases,
            Covered));
    }

    // Design spec §7.5: a key on another shard is emitted only in absolute forms drawn from that shard's registered bases.
    [Fact]
    public void RejectsAForeignFormPolicyThatEmitsRelativeReferences()
    {
        Should.Throw<ArgumentException>(() => new ReferencePathCertification(
            ObservationSubjectPatient,
            ReferencePathOperations.DistributedForward,
            ReferenceSearchForms.Relative,
            ReferenceSearchForms.CanonicalAbsolute | ReferenceSearchForms.Relative,
            Covered));
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
        certification.CoveredRegistryVersion.ShouldBeNull();
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

    [Theory]
    [InlineData(ReferencePathOperations.DistributedForward)]
    [InlineData(ReferencePathOperations.RevIncludeSearch)]
    public void RejectsAReferenceSearchWithoutRegistryCoverage(ReferencePathOperations operations)
    {
        Should.Throw<ArgumentException>(() => new ReferencePathCertification(
            ObservationSubjectPatient,
            operations,
            ReferenceSearchForms.Relative,
            ReferenceSearchForms.CanonicalAbsolute));
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    public void RejectsCoverageThatIsNotARegistryVersion(string version)
    {
        Should.Throw<ArgumentException>(() => new ReferencePathCertification(
            ObservationSubjectPatient,
            ReferencePathOperations.DistributedForward,
            ReferenceSearchForms.Relative,
            ReferenceSearchForms.CanonicalAbsolute,
            version));
    }

    [Fact]
    public void RejectsRegistryCoverageForOperationsThatSearchNoReferences()
    {
        Should.Throw<ArgumentException>(() => new ReferencePathCertification(
            ObservationSubjectPatient,
            ReferencePathOperations.NativeForward | ReferencePathOperations.DistributedReverse | ReferencePathOperations.IncludeExtraction,
            ReferenceSearchForms.None,
            ReferenceSearchForms.None,
            Covered));
    }

    [Theory]
    [InlineData(4 | 64)]
    [InlineData(4 | 128)]
    [InlineData(-1)]
    public void RejectsUndefinedOperations(int operations)
    {
        Should.Throw<ArgumentException>(() => new ReferencePathCertification(
            ObservationSubjectPatient,
            (ReferencePathOperations)operations,
            ReferenceSearchForms.Relative,
            ReferenceSearchForms.CanonicalAbsolute,
            Covered));
    }

    [Theory]
    [InlineData(1 | 8, 2)]
    [InlineData(1, 2 | 16)]
    [InlineData(-1, 2)]
    public void RejectsUndefinedForms(int ownSide, int foreign)
    {
        Should.Throw<ArgumentException>(() => new ReferencePathCertification(
            ObservationSubjectPatient,
            ReferencePathOperations.DistributedForward,
            (ReferenceSearchForms)ownSide,
            (ReferenceSearchForms)foreign,
            Covered));
    }

    [Fact]
    public void AcceptsCanonicalOnlyEmissionWithRegistryCoverage()
    {
        var certification = new ReferencePathCertification(
            ObservationSubjectPatient,
            ReferencePathOperations.DistributedForward | ReferencePathOperations.RevIncludeSearch,
            ReferenceSearchForms.CanonicalAbsolute,
            ReferenceSearchForms.CanonicalAbsolute,
            Covered);

        certification.ForeignForms.ShouldBe(ReferenceSearchForms.CanonicalAbsolute);
        certification.CoveredRegistryVersion.ShouldBe(Covered);
    }

    [Theory]
    [InlineData(ReferencePathOperations.RevIncludeSearch, "Observation?@Observation.subject:Patient&code=1")]
    [InlineData(ReferencePathOperations.DistributedForward, "Observation?@Observation.subject:Patient")]
    public void RejectsRevIncludeEvidenceForTheWrongShapeOrOperation(ReferencePathOperations operations, string shape)
    {
        Should.Throw<ArgumentException>(() => new ReferencePathCertification(
            ObservationSubjectPatient,
            operations,
            ReferenceSearchForms.CanonicalAbsolute,
            ReferenceSearchForms.CanonicalAbsolute,
            Covered,
            revIncludeSearchShape: shape)).ParamName.ShouldBe("revIncludeSearchShape");
    }

    [Theory]
    [InlineData(ReferencePathOperations.DistributedForward, " ")]
    [InlineData(ReferencePathOperations.RevIncludeSearch, "Observation?@Observation.subject:Patient")]
    public void RejectsForwardEvidenceThatIsBlankOrAssignedToTheWrongOperation(ReferencePathOperations operations, string shape)
    {
        Should.Throw<ArgumentException>(() => new ReferencePathCertification(
            ObservationSubjectPatient,
            operations,
            ReferenceSearchForms.CanonicalAbsolute,
            ReferenceSearchForms.CanonicalAbsolute,
            Covered,
            forwardSearchShapes: [shape])).ParamName.ShouldBe("forwardSearchShapes");
    }
}
