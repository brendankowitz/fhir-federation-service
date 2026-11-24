namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// One Patient.given case: the literal request text sent in both GET and POST, the sorted literal match ids
/// the backend must return, and the decoded selection pairs that <c>Bundle.self</c> must echo.
/// </summary>
/// <param name="Label">A reserved short name used in reports and as the xUnit theory key.</param>
/// <param name="WireQuery">The selection text exactly as sent, before the page controls. Escapes are literal.</param>
/// <param name="ExpectedIds">The match ids in ordinal order, written from the requirement and the seed, never from a response.</param>
/// <param name="ExpectedSelection">The decoded name and value of every selection parameter, with multiplicity.</param>
internal sealed record GivenCase(
    string Label,
    string WireQuery,
    IReadOnlyList<string> ExpectedIds,
    IReadOnlyList<(string Name, string Value)> ExpectedSelection);

/// <summary>
/// The six-case Patient.given matrix over <see cref="GivenSeedData"/>. The expected ids are literals derived
/// from FHIR R4 string search (a default string match is a case-insensitive prefix, <c>:exact</c> is a
/// case-sensitive whole match, repeated parameters are AND, a comma is OR). Seed: <c>given-both</c> is
/// Quartz and Mica, <c>given-left</c> Quartz, <c>given-right</c> Mica, <c>given-long</c> Quartzite,
/// <c>given-case</c> QUARTZ, <c>given-neither</c> Topaz.
/// </summary>
internal static class GivenCases
{
    public static IReadOnlyList<GivenCase> All { get; } =
    [
        new("left", "given=Quartz",
            ["given-both", "given-case", "given-left", "given-long"],
            [("given", "Quartz")]),
        new("right", "given=Mica",
            ["given-both", "given-right"],
            [("given", "Mica")]),
        new("and", "given=Quartz&given=Mica",
            ["given-both"],
            [("given", "Quartz"), ("given", "Mica")]),
        new("or", "given=Quartz%2CMica",
            ["given-both", "given-case", "given-left", "given-long", "given-right"],
            [("given", "Quartz,Mica")]),
        new("exact", "given%3Aexact=Quartz",
            ["given-both", "given-left"],
            [("given:exact", "Quartz")]),
        new("exact-upper", "given%3Aexact=QUARTZ",
            ["given-case"],
            [("given:exact", "QUARTZ")]),
    ];

    /// <exception cref="ArgumentOutOfRangeException">No case has that label.</exception>
    public static GivenCase For(string label) =>
        All.SingleOrDefault(c => c.Label == label) ?? throw new ArgumentOutOfRangeException(nameof(label), "No such Patient.given case.");
}
