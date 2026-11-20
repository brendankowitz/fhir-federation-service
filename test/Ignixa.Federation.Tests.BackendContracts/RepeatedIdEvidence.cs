using System.Text.Json;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>Which repeated-<c>_id</c> request shape produced a response.</summary>
public enum RepeatedIdShape
{
    /// <summary>A single page (<c>_count=100</c>) that must hold exactly the seeded Patient.</summary>
    Primary,

    /// <summary>The ordinary <c>_count=0</c> request, whose total must be exactly one.</summary>
    Count,
}

/// <summary>How many of the two sent <c>_id</c> occurrences the backend reported in <c>Bundle.self</c>.</summary>
public enum RepeatedIdEcho
{
    /// <summary>One equal occurrence. A negative characterization: it is not proof that the predicates were preserved.</summary>
    Collapsed,

    /// <summary>Two equal occurrences. It does not show that identical predicates independently affected the result.</summary>
    Echoed,
}

/// <summary>
/// The accounted-for answer to one repeated-<c>_id</c> request. Two facts are kept apart: whether the
/// backend returned the correct Patient and total, and how many decoded <c>_id</c> occurrences
/// <c>Bundle.self</c> holds. A correct result with a collapsed pair is still only a collapsed pair.
/// <see cref="Parse"/> is the only way to obtain one, so an instance always has exactly one unambiguous
/// self link whose every decoded <c>_id</c> name and value equals the seeded one, one or two times.
/// Anything missing, wrong, extra or malformed is a <see cref="BackendEvidenceException"/>, not an outcome.
/// </summary>
public sealed class RepeatedIdEvidence
{
    private readonly RepeatedIdShape _shape;
    private readonly int _matchCount;
    private readonly int _total;
    private readonly bool _seededPatientReturned;
    private readonly int _occurrences;

    private RepeatedIdEvidence(RepeatedIdShape shape, int matchCount, int total, bool seededPatientReturned, int occurrences)
    {
        _shape = shape;
        _matchCount = matchCount;
        _total = total;
        _seededPatientReturned = seededPatientReturned;
        _occurrences = occurrences;
    }

    /// <summary>The primary page holds exactly the seeded Patient with total 1, or the count's total is exactly 1.</summary>
    public bool ResultAsExpected => _total == 1 && (_shape == RepeatedIdShape.Count || (_matchCount == 1 && _seededPatientReturned));

    /// <summary>The echo outcome, or <see langword="null"/> when the result is not the expected one: it is then not reported.</summary>
    public RepeatedIdEcho? Echo => !ResultAsExpected ? null : _occurrences == 2 ? RepeatedIdEcho.Echoed : RepeatedIdEcho.Collapsed;

    /// <param name="json">The response body.</param>
    /// <param name="shape">The request shape that produced it.</param>
    /// <param name="expectedId">The seeded Patient id, which is also the decoded value of every sent <c>_id</c>.</param>
    /// <exception cref="BackendEvidenceException">The response is not a complete searchset of that shape, or its self link cannot be accounted for.</exception>
    public static RepeatedIdEvidence Parse(string json, RepeatedIdShape shape, string expectedId)
    {
        if (shape == RepeatedIdShape.Count)
        {
            int total = CountEvidence.Parse(json).Total;
            return new(shape, 0, total, seededPatientReturned: false, CountSelfOccurrences(json, expectedId));
        }

        IReadOnlyList<string> ids = SearchsetEvidence.Parse(json, "Patient").MatchIds;
        return new(shape, ids.Count, ids.Count, ids.Contains(expectedId, StringComparer.Ordinal), CountSelfOccurrences(json, expectedId));
    }

    /// <summary>
    /// A bounded report of one case. <paramref name="label"/> is a reserved method and shape label chosen by
    /// the test. The report holds counts only, never text from the response or the request.
    /// </summary>
    public string Describe(string label)
    {
        string expected = _shape == RepeatedIdShape.Primary ? "1 match (the seeded Patient), total 1" : "0 entries, total 1";
        string actual = _shape == RepeatedIdShape.Primary
            ? $"{_matchCount} matches (seeded Patient returned: {(_seededPatientReturned ? "yes" : "no")}), total {_total}"
            : $"total {_total}";
        string result = $"[contract] {label}: {(ResultAsExpected ? "matched" : "unsupported match behavior")}. expected: {expected}; actual: {actual}.";

        return Echo switch
        {
            RepeatedIdEcho.Collapsed => $"{result} [characterization] Bundle.self decoded _id occurrences: {_occurrences} (collapsed pair). "
                + "A collapsed occurrence is not proof of preserved predicates, even with the correct result.",
            RepeatedIdEcho.Echoed => $"{result} [characterization] Bundle.self decoded _id occurrences: {_occurrences} (echoed pair). "
                + "An echoed pair does not show that the identical predicates independently affected the result.",
            _ => $"{result} No _id echo outcome is reported.",
        };
    }

    /// <summary>
    /// Counts the <c>_id</c> parameters of the single self link by decoded name and value. The query is split on
    /// <c>&amp;</c> and <c>=</c> before decoding, so an encoded delimiter stays data, and the URL is never requested.
    /// </summary>
    private static int CountSelfOccurrences(string json, string expectedId)
    {
        using JsonDocument document = SearchsetJson.ParseCompleteSearchset(json, countOnly: true);

        string[] selfUrls = [.. SearchsetJson.Array(document.RootElement, "link")
            .Where(link => SearchsetJson.StringMember(link, "relation") == "self")
            .Select(link => SearchsetJson.StringMember(link, "url")!)];
        if (selfUrls.Length != 1)
        {
            throw SearchsetJson.Incomplete($"the searchset holds {selfUrls.Length} self links, not exactly one");
        }

        string url = selfUrls[0];
        if (!Uri.IsWellFormedUriString(url, UriKind.RelativeOrAbsolute))
        {
            throw SearchsetJson.Incomplete("the self link is not a well-formed URI reference");
        }

        int fragmentStart = url.IndexOf('#', StringComparison.Ordinal);
        int queryStart = url.IndexOf('?', StringComparison.Ordinal);
        int queryEnd = fragmentStart < 0 ? url.Length : fragmentStart;
        string query = queryStart >= 0 && queryStart < queryEnd ? url[(queryStart + 1)..queryEnd] : string.Empty;

        int occurrences = 0;
        foreach (string parameter in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = parameter.IndexOf('=', StringComparison.Ordinal);
            string name = Decode(separator < 0 ? parameter : parameter[..separator]);
            if (!string.Equals(name, "_id", StringComparison.Ordinal))
            {
                continue;
            }

            string value = Decode(separator < 0 ? string.Empty : parameter[(separator + 1)..]);
            if (!string.Equals(value, expectedId, StringComparison.Ordinal))
            {
                throw SearchsetJson.Incomplete("the self link holds an _id occurrence whose decoded value is not the seeded id");
            }

            occurrences++;
        }

        return occurrences is 1 or 2
            ? occurrences
            : throw SearchsetJson.Incomplete($"the self link holds {occurrences} decoded _id occurrences, not one or two");
    }

    private static string Decode(string formComponent) => Uri.UnescapeDataString(formComponent.Replace('+', ' '));
}
