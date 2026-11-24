namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>One decoded <c>subject</c> value the backend is asked to select, with a reserved label that a report may print.</summary>
/// <param name="Label">A reserved short name: <c>foreign-a</c> or <c>own</c>.</param>
/// <param name="Value">The decoded reference text. It embeds the dynamic local base, so a report never prints it.</param>
internal sealed record SubjectTerm(string Label, string Value);

/// <summary>
/// One Observation.subject case, the shape a forward join sends: the literal request text sent in both GET and
/// POST, the sorted literal match ids the backend must return, and the decoded <c>subject</c> terms that
/// <c>Bundle.self</c> must echo, with multiplicity.
/// </summary>
/// <param name="Label">A reserved short name used in reports and as the xUnit theory key.</param>
/// <param name="WireQuery">The selection text exactly as sent, before the page controls. Escapes are literal.</param>
/// <param name="ExpectedIds">The match ids in ordinal order, written from the requirement and the seed, never from a response.</param>
/// <param name="ExpectedTerms">The decoded <c>subject</c> terms of the request, with multiplicity.</param>
internal sealed record SubjectJoinCase(
    string Label,
    string WireQuery,
    IReadOnlyList<string> ExpectedIds,
    IReadOnlyList<SubjectTerm> ExpectedTerms)
{
    /// <summary>Two occurrences decode to the same term, so a form POST echo of one or two is only characterized.</summary>
    public bool RepeatsATerm => ExpectedTerms.Select(t => t.Value).Distinct(StringComparer.Ordinal).Count() < ExpectedTerms.Count;
}

/// <summary>
/// The Observation.subject join cases over <see cref="SeedData"/>. The reference text is dynamic because the
/// seed embeds the actual local base, so each case is built for that base. Seed: <c>obs-relative-dup</c>
/// (<c>Patient/dup</c>), <c>obs-own-absolute-dup</c> (own base), <c>obs-foreign-absolute-dup</c> (foreign A),
/// <c>obs-foreign-b-absolute-dup</c> (foreign B), <c>obs-control-other</c> (<c>Patient/other</c>).
/// <list type="bullet">
/// <item><c>foreign-single</c> and <c>own-single</c>: one term, positive controls for the joined cases.</item>
/// <item><c>equal</c>: foreign A twice, spelled differently on the wire (<c>subject</c> and <c>%73ubject</c>, and
/// <c>Patient</c> and <c>%50atient</c>) so both decode to the byte-identical name and value. A reference matched
/// by both occurrences is matched once.</item>
/// <item><c>unequal</c>: foreign A and the own absolute reference to the same <c>Patient/dup</c>. No seeded
/// reference is both, so the AND of the two selects nothing.</item>
/// </list>
/// </summary>
internal static class SubjectJoinCases
{
    public static IReadOnlyList<string> Labels { get; } = ["foreign-single", "own-single", "equal", "unequal"];

    /// <param name="label">One of <see cref="Labels"/>.</param>
    /// <param name="localBaseUrl">The base the test client uses against the backend, with a trailing slash.</param>
    /// <exception cref="ArgumentOutOfRangeException">No case has that label.</exception>
    public static SubjectJoinCase For(string label, string localBaseUrl)
    {
        var foreign = new SubjectTerm("foreign-a", SeedData.ForeignAReference(localBaseUrl));
        var own = new SubjectTerm("own", $"{localBaseUrl}Patient/{SeedData.DuplicatePatientId}");

        string foreignWire = Uri.EscapeDataString(foreign.Value);
        string ownWire = Uri.EscapeDataString(own.Value);

        // The same decoded value with the "P" of "Patient" spelled as the escape %50, so its raw text differs.
        string typeAndId = $"Patient/{SeedData.DuplicatePatientId}";
        string foreignAltWire = Uri.EscapeDataString(foreign.Value[..^typeAndId.Length]) + "%50" + Uri.EscapeDataString(typeAndId[1..]);

        return label switch
        {
            "foreign-single" => new(label, $"subject={foreignWire}", [SeedData.ForeignAbsoluteObservationId], [foreign]),
            "own-single" => new(label, $"subject={ownWire}", [SeedData.OwnAbsoluteObservationId, SeedData.RelativeObservationId], [own]),
            "equal" => new(label, $"subject={foreignWire}&%73ubject={foreignAltWire}", [SeedData.ForeignAbsoluteObservationId], [foreign, foreign]),
            "unequal" => new(label, $"subject={foreignWire}&subject={ownWire}", [], [foreign, own]),
            _ => throw new ArgumentOutOfRangeException(nameof(label), "No such Observation.subject case."),
        };
    }
}
