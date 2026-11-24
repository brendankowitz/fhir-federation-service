using System.Text.Json;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// The accounted-for answer to one Patient.given request. Two facts are kept apart and both are contracts:
/// the sorted match ids of a single complete page, and the decoded selection pairs that <c>Bundle.self</c>
/// echoes with their multiplicity. Neither is derived from the other, so a correct echo cannot excuse a wrong
/// match multiset and a correct multiset cannot excuse a dropped, altered, added or collapsed predicate.
/// <see cref="Parse"/> is the only way to obtain one, so an instance always has a page with no next link, a
/// total equal to its match count, only Patient matches, and exactly one well-formed self link.
/// </summary>
internal sealed class GivenEvidence
{
    private static readonly string[] ControlNames = ["_count", "_total"];

    private readonly SearchsetEvidence _page;
    private readonly (string Name, string Value)[] _selection;

    private GivenEvidence(SearchsetEvidence page, (string Name, string Value)[] selection)
    {
        _page = page;
        _selection = selection;
    }

    /// <param name="json">The response body.</param>
    /// <exception cref="BackendEvidenceException">The response is not a complete Patient searchset, or its self link cannot be accounted for.</exception>
    public static GivenEvidence Parse(string json)
    {
        SearchsetEvidence page = SearchsetEvidence.Parse(json, "Patient");
        using JsonDocument document = SearchsetJson.ParseCompleteSearchset(json, countOnly: true);

        // The page controls are ours and say nothing about which resources were selected.
        (string Name, string Value)[] selection =
        [.. SearchsetJson.SelfQueryPairs(document.RootElement).Where(p => !ControlNames.Contains(p.Name, StringComparer.Ordinal))];
        return new GivenEvidence(page, selection);
    }

    /// <summary>The complete page holds exactly the literal match multiset.</summary>
    public bool MatchesIds(GivenCase given) => Match(given, string.Empty).IsEqual;

    /// <summary>The self link echoes exactly the literal selection pairs: none missing, none unexpected, same multiplicity.</summary>
    public bool EchoesSelection(GivenCase given)
    {
        (string[] missing, int unexpected) = EchoDifference(given);
        return missing.Length == 0 && unexpected == 0;
    }

    public bool IsAsExpected(GivenCase given) => MatchesIds(given) && EchoesSelection(given);

    /// <summary>
    /// A bounded report of one case. <paramref name="label"/> is a reserved method and case label chosen by the
    /// test. It prints reserved ids and the expected pairs, and counts of anything else, never response text.
    /// </summary>
    public string Describe(string label, GivenCase given)
    {
        (string[] missing, int unexpected) = EchoDifference(given);
        string echo = EchoesSelection(given)
            ? "matched"
            : $"unsupported echo behavior (missing: [{string.Join(", ", missing)}]; unexpected: {unexpected} pair(s))";
        return $"{Match(given, label).Describe()}; echo of decoded selection pairs: {echo}";
    }

    private MatchComparison Match(GivenCase given, string label) =>
        MatchComparison.Compare(EvidenceLabel.Contract, label, [.. given.ExpectedIds], _page, GivenSeedData.IsReservedId);

    private (string[] Missing, int Unexpected) EchoDifference(GivenCase given)
    {
        (string, string)[] expected = [.. given.ExpectedSelection];
        (string, string)[] actual = [.. _selection];
        return (
            [.. MatchComparison.MultisetExcept(expected, actual).Select(p => $"{p.Item1}={p.Item2}")],
            MatchComparison.MultisetExcept(actual, expected).Length);
    }
}
