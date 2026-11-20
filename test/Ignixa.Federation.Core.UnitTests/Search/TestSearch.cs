using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Shards;
using Ignixa.Specification.ValueSets.Normative;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Search;

/// <summary>
/// Builds two fully certified shards (east and west) so each planner test removes only the fact it is about.
/// </summary>
internal static class TestSearch
{
    public static readonly SearchParamType[] AllTypes = Enum.GetValues<SearchParamType>();

    public static readonly ReferencePath ObservationSubjectPatient = Path("Observation", "subject", "Patient");
    public static readonly ReferencePath PatientOrganization = Path("Patient", "organization", "Organization");
    public static readonly ReferencePath ObservationPerformerPractitioner = Path("Observation", "performer", "Practitioner");
    public static readonly ReferencePath QuestionnaireResponseQuestionnaire = Path("QuestionnaireResponse", "questionnaire", "Questionnaire");

    public static IReadOnlyDictionary<SearchParamType, IReadOnlyCollection<string>> AllModifiers { get; } =
        new Dictionary<SearchParamType, IReadOnlyCollection<string>>
        {
            [SearchParamType.String] = ["exact", "contains", "missing"],
            [SearchParamType.Token] = ["not", "text", "missing"],
            [SearchParamType.Reference] = [SearchCertification.TypeModifier, "missing", "identifier"],
            [SearchParamType.Date] = ["missing"],
        };

    public static ResourceType Type(string value) => ResourceType.Parse(value);

    public static ReferencePath Path(string source, string code, string target) => new(Type(source), code, Type(target));

    public static SearchParameterSupport Param(string code, SearchParamType type, string? definition = null) =>
        new(code, type, definition is null ? null : new Uri(definition));

    public static IReadOnlyList<ResourceSearchSupport> AllResources { get; } =
    [
        new(
            Type("Patient"),
            [
                Param("_id", SearchParamType.Token),
                Param("_lastUpdated", SearchParamType.Date),
                Param("name", SearchParamType.String, "http://hl7.org/fhir/SearchParameter/Patient-name"),
                Param("family", SearchParamType.String),
                Param("gender", SearchParamType.Token),
                Param("birthdate", SearchParamType.Date),
                Param("organization", SearchParamType.Reference),
                Param("general-practitioner", SearchParamType.Reference),
            ],
            ["Patient:organization", "Patient:general-practitioner"],
            ["Observation:subject", "Observation:performer"]),
        new(
            Type("Observation"),
            [
                Param("_id", SearchParamType.Token),
                Param("code", SearchParamType.Token),
                Param("subject", SearchParamType.Reference),
                Param("patient", SearchParamType.Reference),
                Param("performer", SearchParamType.Reference),
                Param("value-quantity", SearchParamType.Quantity),
                Param("date", SearchParamType.Date),
            ],
            ["Observation:subject:Patient", "Observation:performer", "Observation:patient"],
            []),
        new(Type("Organization"), [Param("_id", SearchParamType.Token), Param("name", SearchParamType.String)], [], ["Patient:organization"]),
        new(Type("Practitioner"), [Param("_id", SearchParamType.Token), Param("name", SearchParamType.String)], [], []),
        new(
            Type("QuestionnaireResponse"),
            [Param("_id", SearchParamType.Token), Param("questionnaire", SearchParamType.Reference)],
            ["QuestionnaireResponse:questionnaire"],
            []),
        new(Type("Questionnaire"), [Param("_id", SearchParamType.Token), Param("name", SearchParamType.String)], [], ["QuestionnaireResponse:questionnaire"]),
    ];

    public static ReferencePathCertification FullPath(ReferencePath path) => new(
        path,
        ReferencePathOperations.NativeForward | ReferencePathOperations.NativeReverse | ReferencePathOperations.DistributedForward
            | ReferencePathOperations.DistributedReverse | ReferencePathOperations.IncludeExtraction | ReferencePathOperations.RevIncludeSearch,
        ReferenceSearchForms.Relative | ReferenceSearchForms.CanonicalAbsolute,
        ReferenceSearchForms.CanonicalAbsolute | ReferenceSearchForms.LinkBaseAliases);

    public static IReadOnlyList<ReferencePathCertification> AllPaths { get; } =
        [FullPath(ObservationSubjectPatient), FullPath(PatientOrganization), FullPath(ObservationPerformerPractitioner), FullPath(QuestionnaireResponseQuestionnaire)];

    public static SearchCertification Certification(
        IReadOnlyDictionary<SearchParamType, IReadOnlyCollection<string>>? modifiers = null,
        IEnumerable<SearchParamType>? and = null,
        IEnumerable<SearchParamType>? or = null,
        IEnumerable<string>? exactCountShapes = null,
        IEnumerable<ReferencePathCertification>? paths = null) =>
        new(modifiers ?? AllModifiers, and ?? AllTypes, or ?? AllTypes, exactCountShapes ?? [], paths ?? AllPaths);

    public static ShardRegistry Registry() => TestShards.Create(TestShards.East(), TestShards.West());

    public static SearchCapabilities Capabilities(
        SearchCertification? east = null,
        SearchCertification? west = null,
        IEnumerable<ResourceSearchSupport>? westResources = null,
        ShardRegistry? registry = null)
    {
        registry ??= Registry();
        return new SearchCapabilities(
            registry,
            [
                new ShardSearchCapability(ShardId.Parse("east"), AllResources, east ?? Certification()),
                new ShardSearchCapability(ShardId.Parse("west"), westResources ?? AllResources, west ?? Certification()),
            ]);
    }

    public static SearchPlanner Planner(SearchCapabilities? capabilities = null, params string[] passthrough)
    {
        var chains = new ChainResolutionOptions();
        foreach (string path in passthrough)
        {
            chains.Passthrough.Add(path);
        }

        return new SearchPlanner(capabilities ?? Capabilities(), chains, new LimitOptions());
    }

    public static SearchQuery Query(string encoded) =>
        SearchQuery.Parse(encoded).Query ?? throw new InvalidOperationException("Test query did not parse.");

    public static SearchPlanResult Plan(string type, string encoded, SearchPlanner? planner = null) =>
        (planner ?? Planner()).Plan(Type(type), Query(encoded));

    public static QueryPlan PlanOk(string type, string encoded, SearchPlanner? planner = null)
    {
        SearchPlanResult result = Plan(type, encoded, planner);
        result.Failure.ShouldBeNull();
        result.Continuation.ShouldBeNull();
        return result.Plan.ShouldNotBeNull();
    }

    public static SearchFailure PlanFails(string type, string encoded, SearchPlanner? planner = null)
    {
        SearchPlanResult result = Plan(type, encoded, planner);
        result.Plan.ShouldBeNull();
        result.Continuation.ShouldBeNull();
        return result.Failure.ShouldNotBeNull();
    }

    /// <summary>Asserts the search fails with <paramref name="code"/> at <paramref name="ordinal"/>.</summary>
    public static SearchFailure ShouldFail(string type, string encoded, SearchFailureCode code, int ordinal = 0, SearchPlanner? planner = null)
    {
        SearchFailure failure = PlanFails(type, encoded, planner);
        failure.Code.ShouldBe(code);
        failure.Ordinal.ShouldBe(ordinal);
        return failure;
    }
}
