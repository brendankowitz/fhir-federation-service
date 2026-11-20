using System.Text.Json;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>How <c>Bundle.self</c> echoed the decoded <c>subject</c> terms of a request.</summary>
internal enum SubjectEchoOutcome
{
    /// <summary>Exactly the requested terms, with their multiplicity.</summary>
    Echoed,

    /// <summary>Each distinct requested term once. Accepted only where the caller characterizes a collapse.</summary>
    Collapsed,

    /// <summary>Anything else: a dropped, altered, added, tripled or unrelated term.</summary>
    Rejected,
}

/// <summary>
/// The accounted-for answer to one Observation.subject join request. Two facts are kept apart: the sorted match
/// ids of a single complete page, and the decoded selection pairs that <c>Bundle.self</c> echoes with their
/// multiplicity. Neither is derived from the other, so a correct echo cannot excuse a wrong match multiset, and a
/// correct multiset cannot excuse a dropped, altered, added or collapsed term. An echo shows what the backend
/// reported, not which terms filtered the result: only the match multiset says that.
/// <see cref="Parse"/> is the only way to obtain one, so an instance always has a page with no next link, a total
/// equal to its match count, only Observation matches, and exactly one well-formed self link.
/// </summary>
internal sealed class SubjectJoinEvidence
{
    private const string SubjectName = "subject";
    private static readonly string[] ControlNames = ["_count", "_total"];

    private readonly SearchsetEvidence _page;
    private readonly (string Name, string Value)[] _selection;

    private SubjectJoinEvidence(SearchsetEvidence page, (string Name, string Value)[] selection)
    {
        _page = page;
        _selection = selection;
    }

    /// <param name="json">The response body.</param>
    /// <exception cref="BackendEvidenceException">The response is not a complete Observation searchset, or its self link cannot be accounted for.</exception>
    public static SubjectJoinEvidence Parse(string json)
    {
        SearchsetEvidence page = SearchsetEvidence.Parse(json, "Observation");
        using JsonDocument document = SearchsetJson.ParseCompleteSearchset(json, countOnly: true);

        // The page controls are ours and say nothing about which resources were selected.
        (string Name, string Value)[] selection =
        [.. SearchsetJson.SelfQueryPairs(document.RootElement).Where(p => !ControlNames.Contains(p.Name, StringComparer.Ordinal))];
        return new SubjectJoinEvidence(page, selection);
    }

    /// <summary>The complete page holds exactly the literal match multiset.</summary>
    public bool MatchesIds(SubjectJoinCase join) => Match(join, string.Empty).IsEqual;

    /// <summary>
    /// Compares the echoed pairs with the requested terms as multisets. One occurrence per distinct term is a
    /// characterized <see cref="SubjectEchoOutcome.Collapsed"/> outcome for a form POST only. For GET it is
    /// rejected, because a join needs every occurrence echoed.
    /// </summary>
    public SubjectEchoOutcome Echo(SubjectJoinCase join, HttpMethod method)
    {
        (string, string)[] expected = Expected(join);
        if (Same(_selection, expected))
        {
            return SubjectEchoOutcome.Echoed;
        }

        return method == HttpMethod.Post && Same(_selection, [.. expected.Distinct()])
            ? SubjectEchoOutcome.Collapsed
            : SubjectEchoOutcome.Rejected;
    }

    /// <summary>The page holds the literal match multiset and the echo is not rejected.</summary>
    public bool IsAsExpected(SubjectJoinCase join, HttpMethod method) =>
        MatchesIds(join) && Echo(join, method) != SubjectEchoOutcome.Rejected;

    /// <summary>
    /// A bounded report of one case. <paramref name="label"/> is a reserved method and case label chosen by the
    /// test. It prints reserved ids, reserved term labels and counts, never a reference value, query or response text.
    /// </summary>
    public string Describe(string label, SubjectJoinCase join, HttpMethod method)
    {
        // Both accepted echoes of a repeated term in a form POST are recorded, not required.
        bool characterized = method == HttpMethod.Post && join.RepeatsATerm;
        string echo = Echo(join, method) switch
        {
            SubjectEchoOutcome.Echoed when characterized => $"characterization only: every occurrence echoed ({Counts(join, observed: true)})",
            SubjectEchoOutcome.Echoed => "matched",
            SubjectEchoOutcome.Collapsed => $"characterization only: collapsed to one occurrence per term ({Counts(join, observed: true)})",
            _ => $"unsupported echo behavior (observed: {Counts(join, observed: true)}; expected: {Counts(join, observed: false)})",
        };
        return $"{Match(join, label).Describe()}; echo of decoded selection pairs: {echo}";
    }

    private MatchComparison Match(SubjectJoinCase join, string label) =>
        MatchComparison.Compare(EvidenceLabel.Contract, label, [.. join.ExpectedIds], _page);

    private static (string, string)[] Expected(SubjectJoinCase join) => [.. join.ExpectedTerms.Select(t => (SubjectName, t.Value))];

    private static bool Same((string, string)[] actual, (string, string)[] expected) =>
        MatchComparison.MultisetExcept(expected, actual).Length == 0 && MatchComparison.MultisetExcept(actual, expected).Length == 0;

    /// <summary>The count of each requested term by label, then the count of every other echoed pair.</summary>
    private string Counts(SubjectJoinCase join, bool observed)
    {
        string[] labels = [.. join.ExpectedTerms.Select(t => t.Label).Distinct(StringComparer.Ordinal)];
        (string, string)[] pairs = observed ? _selection : Expected(join);
        int known = 0;
        var parts = new List<string>();
        foreach (string label in labels)
        {
            string value = join.ExpectedTerms.First(t => t.Label == label).Value;
            int count = pairs.Count(p => p == (SubjectName, value));
            known += count;
            parts.Add($"{label} x{count}");
        }

        if (observed)
        {
            parts.Add($"other x{pairs.Length - known}");
        }

        return string.Join(", ", parts);
    }
}
